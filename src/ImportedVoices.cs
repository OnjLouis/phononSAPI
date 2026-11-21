// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PhononSapi {
    internal sealed class ImportedVoices {
        const int MaxBytes=4*1024*1024, MaxMetadata=512, MaxVoices=64, MaxName=80;
        const string Format="PhononImport/1";
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        static readonly Regex Id=new Regex(@"\Aimported-[0-9a-f]{32}\z",RegexOptions.CultureInvariant);
        internal static readonly string[] Locales={"en-GB","en-US","fr-FR","de-DE","es-ES","es-MX","pt-BR"};
        readonly string folder, install;

        internal ImportedVoices(string data,string install) {folder=Path.Combine(data,"ImportedVoices");this.install=install;}
        internal static bool IsId(string id) {return id!=null && Id.IsMatch(id);}
        static bool IsPlain(FileAttributes attributes) {return (attributes&FileAttributes.ReparsePoint)==0;}
        string PathFor(string id,string extension) {return Path.Combine(folder,id+extension);}
        static bool ValidName(string name) {
            return !String.IsNullOrWhiteSpace(name) && name.Length<=MaxName && !name.Any(Char.IsControl);
        }
        static byte[] Read(string file,int limit) {
            var attrs=File.GetAttributes(file);
            if(!IsPlain(attrs) || (attrs&FileAttributes.Directory)!=0)throw new InvalidDataException("Choose an ordinary voice file.");
            using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                long length=stream.Length;
                if(length<=0 || length>limit)throw new InvalidDataException("The voice file size is not supported.");
                var bytes=new byte[(int)length];
                int count=0;
                while(count<bytes.Length) {
                    int read=stream.Read(bytes,count,bytes.Length-count);
                    if(read==0)throw new EndOfStreamException();
                    count+=read;
                }
                return bytes;
            }
        }
        internal IEnumerable<Voice> List() {
            if(!Directory.Exists(folder) || !IsPlain(File.GetAttributes(folder)))return new Voice[0];
            var result=new List<Voice>();
            foreach(var file in Directory.EnumerateFiles(folder,"imported-*.voice").Take(512)) {
                var id=Path.GetFileNameWithoutExtension(file);
                if(!IsId(id))continue;
                try {
                    var tensor=new FileInfo(PathFor(id,".safetensors"));
                    if(!tensor.Exists || !IsPlain(tensor.Attributes) || tensor.Length<=0 || tensor.Length>MaxBytes)continue;
                    var fields=Utf8.GetString(Read(file,MaxMetadata)).Split('\n');
                    if(fields.Length==3 && fields[0]==Format && ValidName(fields[1]) && Locales.Contains(fields[2]))
                        result.Add(new Voice {id=id,name=fields[1],locale=fields[2],language=fields[2].Substring(0,2)});
                } catch(IOException){} catch(UnauthorizedAccessException){} catch(DecoderFallbackException){}
            }
            return result.OrderBy(v=>v.name,StringComparer.CurrentCultureIgnoreCase).ThenBy(v=>v.id,StringComparer.Ordinal).ToArray();
        }
        internal static byte[] ReadSource(string source) {
            if(!String.Equals(Path.GetExtension(source),".safetensors",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Choose a Phonon .safetensors voice file.");
            return Read(source,MaxBytes);
        }
        internal byte[] ReadVoice(Voice voice) {return Read(PathFor(voice.id,".safetensors"),MaxBytes);}
        internal string Host {get {return Path.Combine(install,"PhononHost.exe");}}
        internal string Add(string source,string name,string locale) {return Add(ReadSource(source),name,locale);}
        internal string Add(byte[] bytes,string name,string locale) {
            name=(name??"").Trim();
            if(!ValidName(name) || !Locales.Contains(locale))throw new InvalidDataException("Enter a voice name and select its language.");
            Directory.CreateDirectory(folder);
            if(!IsPlain(File.GetAttributes(folder)))throw new IOException("The imported voices folder is redirected.");
            if(List().Count()>=MaxVoices)throw new InvalidOperationException("Phonon can store up to 64 imported voices.");
            Validate(bytes);
            byte[] metadata=Utf8.GetBytes(Format+"\n"+name+"\n"+locale);
            var id="imported-"+Guid.NewGuid().ToString("N");
            string tensor=PathFor(id,".safetensors"), record=PathFor(id,".voice");
            Commit(tensor,bytes);
            try {Commit(record,metadata);}
            catch {File.Delete(tensor);throw;}
            return id;
        }
        internal void ChangeLanguage(string id,string locale) {
            var voice=List().FirstOrDefault(item=>item.id==id);
            if(voice==null || !Locales.Contains(locale))throw new InvalidDataException("Choose a supported spoken language.");
            if(voice.locale!=locale)
                Commit(PathFor(id,".voice"),Utf8.GetBytes(Format+"\n"+voice.name+"\n"+locale),true);
        }
        static void Commit(string path,byte[] bytes,bool replace=false) {
            string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                    stream.Write(bytes,0,bytes.Length);stream.Flush(true);
                }
                if(replace)File.Replace(temporary,path,null);else File.Move(temporary,path);
            } finally {if(File.Exists(temporary))File.Delete(temporary);}
        }
        internal void Remove(string id) {
            if(!IsId(id) || !Directory.Exists(folder) || !IsPlain(File.GetAttributes(folder)))
                throw new InvalidOperationException("This is not an imported voice.");
            File.Delete(PathFor(id,".voice"));
            File.Delete(PathFor(id,".safetensors"));
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        delegate bool Validator(byte[] model,byte[] bytes,UIntPtr length);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern IntPtr LoadLibraryEx(string name,IntPtr file,uint flags);
        [DllImport("kernel32.dll",CharSet=CharSet.Ansi,ExactSpelling=true)]
        static extern IntPtr GetProcAddress(IntPtr module,string name);
        [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
        void Validate(byte[] bytes) {
            const uint SearchDllDirectory=0x100, SearchSystem32=0x800;
            var dll=LoadLibraryEx(Path.Combine(install,"runtime","ptts_ffi.dll"),IntPtr.Zero,SearchDllDirectory|SearchSystem32);
            if(dll==IntPtr.Zero)throw new InvalidOperationException("The Phonon voice validator could not start.");
            try {
                var address=GetProcAddress(dll,"ptts_validate_imported_voice");
                if(address==IntPtr.Zero)throw new InvalidOperationException("The Phonon voice validator is missing.");
                var validate=(Validator)Marshal.GetDelegateForFunctionPointer(address,typeof(Validator));
                if(!validate(Utf8.GetBytes(Path.Combine(install,"model")+"\0"),bytes,new UIntPtr((uint)bytes.Length)))
                    throw new InvalidDataException("This file is not compatible with the installed Phonon model.");
            } finally {FreeLibrary(dll);}
        }
    }

    internal sealed class ManageImportedVoicesDialog : Form {
        readonly ImportedVoices store;
        readonly ListBox voices=new ListBox {Height=200};
        readonly Button add=new Button {Text="&Add Voice...",AutoSize=true,TabIndex=0};
        readonly Button edit=new Button {Text="&Change Language...",AutoSize=true,Enabled=false,TabIndex=1};
        readonly Button remove=new Button {Text="&Remove Voice",AutoSize=true,Enabled=false,TabIndex=2};
        internal bool Changed {get;private set;}
        internal string LastAdded {get;private set;}
        internal string LastAddedLocale {get;private set;}
        internal string EditedLocale(string id) {
            string locale;
            return id!=null && edited.TryGetValue(id,out locale)?locale:null;
        }
        readonly Dictionary<string,string> edited=new Dictionary<string,string>(StringComparer.Ordinal);

        internal ManageImportedVoicesDialog(ImportedVoices store,string selected) {
            this.store=store;
            Text="Manage Imported Voices";StartPosition=FormStartPosition.CenterParent;
            FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
            ClientSize=new Size(530,285);
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,Padding=new Padding(10)};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            Controls.Add(layout);
            MainForm.Add(layout,"&Imported voices:",voices);
            var done=new Button {Text="&Done",DialogResult=DialogResult.OK,AutoSize=true,TabIndex=3};
            var actions=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill};
            actions.Controls.AddRange(new Control[]{add,edit,remove,done});MainForm.Add(layout,"",actions);
            CancelButton=done;
            voices.SelectedIndexChanged+=(s,e)=>edit.Enabled=remove.Enabled=voices.SelectedItem!=null;
            add.Click+=(s,e)=>AddVoice(); edit.Click+=(s,e)=>EditLanguage(); remove.Click+=(s,e)=>RemoveVoice();
            RefreshList(selected,0);
            Shown+=(s,e)=>FocusListOrAdd();
        }

        void FocusListOrAdd() {if(voices.Items.Count==0)add.Focus();else voices.Focus();}
        void RefreshList(string preferred,int fallbackIndex) {
            Voice[] entries;
            try {entries=store.List().ToArray();}
            catch(IOException error){ShowError(error);return;}
            catch(UnauthorizedAccessException error){ShowError(error);return;}
            voices.BeginUpdate();
            try {
                voices.Items.Clear();voices.Items.AddRange(entries);
                var wanted=entries.FirstOrDefault(v=>v.id==preferred);
                if(wanted!=null)voices.SelectedItem=wanted;
                else if(entries.Length>0)voices.SelectedIndex=Math.Min(fallbackIndex,entries.Length-1);
                edit.Enabled=remove.Enabled=voices.SelectedItem!=null;
            } finally {voices.EndUpdate();}
        }
        void AddVoice() {
            using(var picker=new OpenFileDialog {Title="Choose a Phonon voice file",Filter="Phonon voice files (*.safetensors)|*.safetensors",CheckFileExists=true,RestoreDirectory=true}) {
                if(picker.ShowDialog(this)!=DialogResult.OK)return;
                try {
                    var bytes=ImportedVoices.ReadSource(picker.FileName);
                    using(var details=new ImportVoiceDialog(store.Host,bytes,Path.GetFileNameWithoutExtension(picker.FileName),null)) {
                        if(details.ShowDialog(this)!=DialogResult.OK)return;
                        LastAdded=store.Add(bytes,details.VoiceName,details.Locale);
                        LastAddedLocale=details.Locale;Changed=true;
                        RefreshList(LastAdded,0);FocusListOrAdd();
                    }
                } catch(Exception error) {ShowError(error);}
            }
        }
        void EditLanguage() {
            var voice=voices.SelectedItem as Voice;
            if(voice==null)return;
            try {
                using(var details=new ImportVoiceDialog(store.Host,store.ReadVoice(voice),voice.name,voice.locale)) {
                    if(details.ShowDialog(this)!=DialogResult.OK)return;
                    store.ChangeLanguage(voice.id,details.Locale);
                    edited[voice.id]=details.Locale;
                    if(LastAdded==voice.id)LastAddedLocale=details.Locale;
                    Changed=true;RefreshList(voice.id,0);FocusListOrAdd();
                }
            } catch(Exception error) {ShowError(error);}
        }
        void RemoveVoice() {
            var selected=voices.SelectedItem as Voice;
            if(selected==null)return;
            if(MessageBox.Show(this,"Remove "+selected.name+" from Phonon? The original voice file will not be changed.","Remove Voice",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
            int index=voices.SelectedIndex;
            Changed=true;
            try {store.Remove(selected.id);}
            catch(Exception error){ShowError(error);}
            if(LastAdded==selected.id)LastAdded=null;
            RefreshList(null,index);FocusListOrAdd();
        }
        void ShowError(Exception error) {MessageBox.Show(this,error.Message,"Imported Voices",MessageBoxButtons.OK,MessageBoxIcon.Error);}
    }

    internal sealed class ImportVoiceDialog : Form {
        readonly TextBox name=new TextBox {MaxLength=80};
        readonly ComboBox language=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList};
        readonly Button preview=new Button {Text="&Preview Voice",AutoSize=true,TabIndex=0};
        readonly Button stop=new Button {Text="S&top Preview",AutoSize=true,Enabled=false,TabIndex=1};
        Process current;
        int previewSequence;
        SoundPlayer player;
        MemoryStream audio;
        internal string VoiceName {get {return name.Text;}}
        internal string Locale {get {var choice=language.SelectedItem as LanguageChoice;return choice==null?null:choice.Locale;}}
        internal ImportVoiceDialog(string host,byte[] bytes,string voiceName,string locale) {
            Text=locale==null?"Import Phonon Voice":"Change Language: "+voiceName;StartPosition=FormStartPosition.CenterParent;
            FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
            ClientSize=new Size(510,180);
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,Padding=new Padding(10)};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            Controls.Add(layout);
            if(locale==null)MainForm.Add(layout,"&Voice name:",name);
            MainForm.Add(layout,"Spoken &language:",language);
            language.Items.Add("Choose a language");
            foreach(var item in ImportedVoices.Locales)language.Items.Add(new LanguageChoice{Locale=item});
            language.SelectedItem=language.Items.OfType<LanguageChoice>().FirstOrDefault(item=>item.Locale==locale);
            if(language.SelectedIndex<0)language.SelectedIndex=0;
            name.Text=voiceName.Substring(0,Math.Min(name.MaxLength,voiceName.Length));
            var import=new Button {Text=locale==null?"&Import":"&Save",DialogResult=DialogResult.OK,AutoSize=true,Enabled=Locale!=null,TabIndex=2};
            var cancel=new Button {Text="&Cancel",DialogResult=DialogResult.Cancel,AutoSize=true,TabIndex=3};
            var actions=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill};
            actions.Controls.AddRange(new Control[]{preview,stop,import,cancel});MainForm.Add(layout,"",actions);
            AcceptButton=import;CancelButton=cancel;ActiveControl=locale==null?(Control)name:language;
            language.SelectedIndexChanged+=(s,e)=>import.Enabled=Locale!=null;
            preview.Click+=async(s,e)=>await PlayPreview(host,bytes);
            stop.Click+=(s,e)=>{StopPreview();preview.Focus();};
            FormClosing+=(s,e)=>StopPreview();
        }

        async Task PlayPreview(string host,byte[] bytes) {
            StopPreview();
            int sequence=previewSequence;
            var sample=new PreviewSample();
            string locale=Locale??"en-US";
            sample.Reset(locale);
            var text=Encoding.UTF8.GetBytes(sample.Text);
            using(var process=new Process {StartInfo=new ProcessStartInfo(host,"--preview") {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true}}) {
                try {
                    if(!process.Start())throw new IOException();
                    current=process;preview.Enabled=false;stop.Enabled=true;stop.Focus();
                    var render=Task.Run(()=>Render(process,bytes,locale,text));
                    if(await Task.WhenAny(render,Task.Delay(TimeSpan.FromSeconds(30)))!=render) {
                        if(sequence==previewSequence && !process.HasExited)process.Kill();
                        try {await render;} catch(IOException){} catch(InvalidOperationException){}
                        throw new IOException();
                    }
                    byte[] rendered=await render;
                    if(current!=process || IsDisposed)return;
                    current=null;
                    audio=new MemoryStream(rendered);player=new SoundPlayer(audio);player.Play();
                    preview.Enabled=true;
                } catch(Exception) {
                    if(sequence!=previewSequence || IsDisposed)return;
                    StopPreview();
                    MessageBox.Show(this,"Phonon could not preview this voice.","Phonon Voices",MessageBoxButtons.OK,MessageBoxIcon.Error);
                }
            }
        }

        static byte[] Render(Process process,byte[] voice,string locale,byte[] text) {
            using(var input=new BinaryWriter(process.StandardInput.BaseStream)) {
                input.Write(voice.Length);input.Write(Encoding.ASCII.GetBytes(locale.Substring(0,2)));
                input.Write(voice);input.Write(text.Length);input.Write(text);
            }
            using(var wave=new MemoryStream())
            using(var data=new BinaryWriter(wave)) {
                data.Write(new byte[44]);
                var pcm=process.StandardOutput.BaseStream;
                var buffer=new byte[8192];
                int count;
                while((count=pcm.Read(buffer,0,buffer.Length))!=0) {
                    if(wave.Length+count>44+24000*2*20)throw new InvalidDataException();
                    data.Write(buffer,0,count);
                }
                process.WaitForExit();
                int length=(int)wave.Length-44;
                if(process.ExitCode!=0 || length==0 || length%2!=0)throw new IOException();
                wave.Position=0;
                data.Write(Encoding.ASCII.GetBytes("RIFF"));data.Write(36+length);
                data.Write(Encoding.ASCII.GetBytes("WAVEfmt "));data.Write(16);data.Write((short)1);data.Write((short)1);
                data.Write(24000);data.Write(48000);data.Write((short)2);data.Write((short)16);
                data.Write(Encoding.ASCII.GetBytes("data"));data.Write(length);
                return wave.ToArray();
            }
        }

        void StopPreview() {
            ++previewSequence;
            var process=current;current=null;
            if(process!=null)try {if(!process.HasExited)process.Kill();} catch(InvalidOperationException){} catch(System.ComponentModel.Win32Exception){}
            if(player!=null){player.Stop();player.Dispose();player=null;}
            if(audio!=null){audio.Dispose();audio=null;}
            preview.Enabled=true;stop.Enabled=false;
        }
    }
}
