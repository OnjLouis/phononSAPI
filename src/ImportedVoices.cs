// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
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
        internal string Add(string source,string name,string locale) {
            name=(name??"").Trim();
            if(!ValidName(name) || !Locales.Contains(locale))throw new InvalidDataException("Enter a voice name and select its language.");
            if(!String.Equals(Path.GetExtension(source),".safetensors",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Choose a Phonon .safetensors voice file.");
            Directory.CreateDirectory(folder);
            if(!IsPlain(File.GetAttributes(folder)))throw new IOException("The imported voices folder is redirected.");
            if(List().Count()>=MaxVoices)throw new InvalidOperationException("Phonon can store up to 64 imported voices.");
            byte[] bytes=Read(source,MaxBytes);
            Validate(bytes);
            byte[] metadata=Utf8.GetBytes(Format+"\n"+name+"\n"+locale);
            var id="imported-"+Guid.NewGuid().ToString("N");
            string tensor=PathFor(id,".safetensors"), record=PathFor(id,".voice");
            Commit(tensor,bytes);
            try {Commit(record,metadata);}
            catch {File.Delete(tensor);throw;}
            return id;
        }
        static void Commit(string path,byte[] bytes) {
            string temporary=path+".tmp";
            try {
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                    stream.Write(bytes,0,bytes.Length);stream.Flush(true);
                }
                File.Move(temporary,path);
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
        readonly string initialLocale;
        readonly ListBox voices=new ListBox {Height=200};
        readonly Button add=new Button {Text="&Add Voice...",AutoSize=true,TabIndex=0};
        readonly Button remove=new Button {Text="&Remove Voice",AutoSize=true,Enabled=false,TabIndex=1};
        internal bool Changed {get;private set;}
        internal string LastAdded {get;private set;}
        internal string LastAddedLocale {get;private set;}

        internal ManageImportedVoicesDialog(ImportedVoices store,string selected,string locale) {
            this.store=store;initialLocale=locale;
            Text="Manage Imported Voices";StartPosition=FormStartPosition.CenterParent;
            FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
            ClientSize=new Size(530,285);
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,Padding=new Padding(10)};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            Controls.Add(layout);
            MainForm.Add(layout,"&Imported voices:",voices);
            var done=new Button {Text="&Done",DialogResult=DialogResult.OK,AutoSize=true,TabIndex=2};
            var actions=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill};
            actions.Controls.AddRange(new Control[]{add,remove,done});MainForm.Add(layout,"",actions);
            CancelButton=done;
            voices.SelectedIndexChanged+=(s,e)=>remove.Enabled=voices.SelectedItem!=null;
            add.Click+=(s,e)=>AddVoice();remove.Click+=(s,e)=>RemoveVoice();
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
                remove.Enabled=voices.SelectedItem!=null;
            } finally {voices.EndUpdate();}
        }
        void AddVoice() {
            using(var picker=new OpenFileDialog {Title="Choose a Phonon voice file",Filter="Phonon voice files (*.safetensors)|*.safetensors",CheckFileExists=true,RestoreDirectory=true}) {
                if(picker.ShowDialog(this)!=DialogResult.OK)return;
                using(var details=new ImportVoiceDialog(picker.FileName,initialLocale)) {
                    if(details.ShowDialog(this)!=DialogResult.OK)return;
                    try {
                        LastAdded=store.Add(picker.FileName,details.VoiceName,details.Locale);
                        LastAddedLocale=details.Locale;Changed=true;
                        RefreshList(LastAdded,0);FocusListOrAdd();
                    } catch(Exception error) {ShowError(error);}
                }
            }
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
        internal string VoiceName {get {return name.Text;}}
        internal string Locale {get {return ((LanguageChoice)language.SelectedItem).Locale;}}
        internal ImportVoiceDialog(string file,string locale) {
            Text="Import Phonon Voice";StartPosition=FormStartPosition.CenterParent;
            FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
            ClientSize=new Size(455,140);
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,Padding=new Padding(10)};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            Controls.Add(layout);
            MainForm.Add(layout,"&Voice name:",name);
            MainForm.Add(layout,"Spoken &language:",language);
            foreach(var item in ImportedVoices.Locales)language.Items.Add(new LanguageChoice{Locale=item});
            language.SelectedItem=language.Items.Cast<LanguageChoice>().FirstOrDefault(item=>item.Locale==locale);
            if(language.SelectedIndex<0)language.SelectedIndex=0;
            var stem=Path.GetFileNameWithoutExtension(file);
            name.Text=stem.Substring(0,Math.Min(name.MaxLength,stem.Length));
            var import=new Button {Text="&Import",DialogResult=DialogResult.OK,AutoSize=true};
            var cancel=new Button {Text="&Cancel",DialogResult=DialogResult.Cancel,AutoSize=true};
            var actions=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill};
            actions.Controls.AddRange(new Control[]{import,cancel});MainForm.Add(layout,"",actions);
            AcceptButton=import;CancelButton=cancel;ActiveControl=name;
        }
    }
}
