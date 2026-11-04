// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Phonon SAPI")]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]

namespace PhononSapi {
    public sealed class Voice {
        public string id { get; set; }
        public string name { get; set; }
        public string locale { get; set; }
        public string language { get; set; }
        public string lcid { get; set; }
        public string gender { get; set; }
        public string legacyId { get; set; }
        public override string ToString() { return name + " (" + App.LanguageName(locale) + ")"; }
    }
    public sealed class LanguageChoice {
        public string Locale;
        public override string ToString() { return Locale==null?"All languages":App.LanguageName(Locale); }
    }
    public sealed class Preferences {
        public bool Gpu = true;
        public string Interval = "daily";
        public DateTime LastCheck = DateTime.MinValue;
        public string Voice = "";
        public int ControlScale = 2;
        public int Rate = 50;
        public int Pitch = 50;
        public int Volume = 100;
    }
    public static class App {
        public const string Version = "1.0.1";
        internal const string UpgradeCode = "{7688E43D-5355-50FC-AFF3-541E76F34049}";
        internal const string SoftwareUrl = "https://onj.me/software";
        internal const string DonationUrl = "https://onj.me/donate";
        public const string ClassId = "{5261CA16-3C40-4AD8-8EC6-4D4650485341}";
        public const string ProductKey = @"Software\Onj\PhononSAPI";
        public const string Tokens = @"SOFTWARE\Microsoft\Speech\Voices\Tokens";
        public static readonly string Folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        public static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhononSAPI");
        public static readonly string InstallRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PhononSAPI");
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };
        public static string PreferencesFile { get { return Path.Combine(Data, "settings.json"); } }
        public static void Log(string message) {
            try {
                Directory.CreateDirectory(Data);
                string file = Path.Combine(Data,"PhononSAPI.log");
                if (File.Exists(file) && new FileInfo(file).Length > 256 * 1024) {
                    if(File.Exists(file+".old")) File.Delete(file+".old");
                    File.Move(file, file + ".old");
                }
                File.AppendAllText(file, DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine);
            } catch { }
        }
        public static Preferences LoadPreferences() {
            try { return ParsePreferences(File.ReadAllText(PreferencesFile)); }
            catch { return new Preferences(); }
        }
        internal static Preferences ParsePreferences(string json) {
            var fields=Json.Deserialize<Dictionary<string,object>>(json);
            var result=Json.Deserialize<Preferences>(json) ?? new Preferences();
            if(fields!=null && !fields.ContainsKey("ControlScale")) {
                result.Rate=(fields.ContainsKey("Rate")?Math.Max(-10,Math.Min(10,Convert.ToInt32(fields["Rate"],CultureInfo.InvariantCulture))):0)*5+50;
                result.Pitch=(fields.ContainsKey("Pitch")?Math.Max(-10,Math.Min(10,Convert.ToInt32(fields["Pitch"],CultureInfo.InvariantCulture))):0)*5+50;
            }
            result.ControlScale=2;
            result.Rate=Math.Max(0,Math.Min(100,result.Rate));result.Pitch=Math.Max(0,Math.Min(100,result.Pitch));
            result.Volume=Math.Max(0,Math.Min(100,result.Volume));return result;
        }
        internal static int ToSapiControl(int percent) {
            return (int)Math.Round((Math.Max(0,Math.Min(100,percent))-50)/5.0,MidpointRounding.AwayFromZero);
        }
        internal static string ReadDiagnostics(string folder) {
            const int MaximumTailBytes=64*1024;
            var result=new StringBuilder();
            foreach(string name in new[]{"PhononSAPI.log","host.log"}) {
                result.AppendLine(name);result.AppendLine(new string('-',name.Length));
                try {
                    using(var file=new FileStream(Path.Combine(folder,name),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
                        int count=(int)Math.Min(MaximumTailBytes,file.Length);byte[] bytes=new byte[count];
                        long start=file.Length-count;file.Seek(start,SeekOrigin.Begin);int received=0;
                        while(received<count){int n=file.Read(bytes,received,count-received);if(n==0)break;received+=n;}
                        string tail=Encoding.UTF8.GetString(bytes,0,received);
                        if(start>0){int line=tail.IndexOf('\n');if(line>=0)tail=tail.Substring(line+1);}
                        result.AppendLine(tail);
                    }
                } catch(FileNotFoundException){result.AppendLine("No log has been written yet.");}
                  catch(DirectoryNotFoundException){result.AppendLine("No log has been written yet.");}
                  catch(IOException error){result.AppendLine("Could not read this log: "+error.Message);}
                  catch(UnauthorizedAccessException error){result.AppendLine("Could not read this log: "+error.Message);}
                result.AppendLine();
            }
            return result.ToString();
        }
        public static void Save(Preferences p) {
            Directory.CreateDirectory(Data);
            string temp = PreferencesFile + ".tmp";
            File.WriteAllText(temp,Json.Serialize(p),new UTF8Encoding(false));
            if (File.Exists(PreferencesFile)) File.Replace(temp,PreferencesFile,null);
            else File.Move(temp,PreferencesFile);
            using (var key = Registry.CurrentUser.CreateSubKey(ProductKey)) key.SetValue("UseGpu",p.Gpu ? 1 : 0,RegistryValueKind.DWord);
        }
        public static List<Voice> Voices(string folder) {
            var voices = Json.Deserialize<List<Voice>>(File.ReadAllText(Path.Combine(folder,"voices.json")));
            if (voices == null || voices.Count != 102 || voices.Select(v => v.id).Distinct().Count() != voices.Count)
                throw new InvalidDataException("The voice catalogue is incomplete.");
            foreach (var v in voices) {
                if (!System.Text.RegularExpressions.Regex.IsMatch(v.id ?? "", @"^PhononSAPI_0[0-7]_[a-z0-9]+_[0-9a-f]{16}$") ||
                    !System.Text.RegularExpressions.Regex.IsMatch(v.legacyId ?? "", @"^PhononSAPI_[0-9a-f]{16}$") ||
                    String.IsNullOrWhiteSpace(v.name) || v.name.IndexOfAny(new[] {'/','\\','\0'}) >= 0 ||
                    !new[] {"en","de","es","fr","pt"}.Contains(v.language))
                    throw new InvalidDataException("Invalid voice catalogue.");
            }
            return voices;
        }
        public static string LanguageName(string locale) {
            switch(locale) {
                case "en-GB":return "English UK";case "en-US":return "English US";case "en":return "English Character Voices";
                case "fr-FR":return "French";case "de-DE":return "German";case "pt-BR":return "Portuguese Brazil";
                case "es-ES":return "Spanish Spain";case "es-MX":return "Spanish Mexico";default:return locale;
            }
        }
        [STAThread] public static int Main(string[] args) {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try {
                if (args.Length == 1 && args[0] == "--check-package") { Installer.Validate(Folder); return 0; }
                if (args.Length != 0) throw new ArgumentException("Unknown command line option.");
                Application.Run(new MainForm()); return 0;
            } catch (Exception error) {
                Log(error.ToString());
                if (args.Contains("--check-package")) return 1;
                MessageBox.Show(error.Message,"Phonon SAPI",MessageBoxButtons.OK,MessageBoxIcon.Error); return 1;
            }
        }
    }

    internal static class BackendStatus {
        internal static string Describe(int[] states,bool requestedGpu,bool unknownActive) {
            var names=states.Distinct().OrderBy(x=>x).Select(x=>x==1?"Initializing":x==2?"GPU (Vulkan)":x==3?"CPU (GPU disabled)":x==4?"CPU fallback (GPU initialization unavailable)":x==5?"Initialization failed":"Unknown backend").ToArray();
            string text=names.Length==0?(unknownActive?"Current engine has not reported its backend.":"Not initialized."):String.Join("; ",names)+".";
            if(names.Length>0 && unknownActive)text+=" Another engine has not reported its backend.";
            if((requestedGpu && states.Contains(3)) || (!requestedGpu && states.Any(x=>x==2 || x==4)))text+=" GPU preference changes apply on the next utterance.";
            return text;
        }
        internal static string Current(bool requestedGpu) {
            var states=new List<int>();bool unknown=false;
            foreach(var process in Process.GetProcessesByName("PhononHost").Take(64))using(process) {
                try {
                    string path=process.MainModule.FileName;
                    string folder=Path.GetDirectoryName(path);
                    if(!String.Equals(folder,App.Folder,StringComparison.OrdinalIgnoreCase) && !String.Equals(folder,App.InstallRoot,StringComparison.OrdinalIgnoreCase))continue;
                    try {
                        using(var mapping=MemoryMappedFile.OpenExisting("Local\\PhononSAPI.Backend."+process.Id,MemoryMappedFileRights.Read))
                        using(var view=mapping.CreateViewAccessor(0,24,MemoryMappedFileAccess.Read)) {
                            if(view.ReadUInt32(0)!=0x42534850 || view.ReadUInt32(4)!=1 ||
                               view.ReadUInt64(8)!=(ulong)process.StartTime.ToUniversalTime().ToFileTimeUtc() || view.ReadUInt32(20)!=0){unknown=true;continue;}
                            int state=view.ReadInt32(16);if(state<1 || state>5){unknown=true;continue;}states.Add(state);
                        }
                    }catch(FileNotFoundException){unknown=true;}
                }catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}catch(UnauthorizedAccessException){unknown=true;}catch(IOException){unknown=true;}
            }
            return Describe(states.ToArray(),requestedGpu,unknown);
        }
    }

    internal sealed class DiagnosticsDialog : Form {
        readonly string folder;
        readonly TextBox details=new TextBox {ReadOnly=true,Multiline=true,WordWrap=false,ScrollBars=ScrollBars.Both,Dock=DockStyle.Fill,AccessibleName="Recent diagnostic logs"};
        public DiagnosticsDialog(string path) {
            folder=path;Text="Phonon SAPI diagnostics";Size=new Size(850,550);MinimumSize=new Size(500,350);
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,RowCount=2,ColumnCount=1,Padding=new Padding(10)};
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(details,0,0);
            var buttons=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill};
            var refresh=new Button {Text="&Refresh"};var copy=new Button {Text="&Copy"};var close=new Button {Text="C&lose",DialogResult=DialogResult.Cancel};
            buttons.Controls.AddRange(new Control[]{refresh,copy,close});layout.Controls.Add(buttons,0,1);Controls.Add(layout);CancelButton=close;
            refresh.Click+=(s,e)=>RefreshLog();copy.Click+=(s,e)=>{try{Clipboard.SetText(details.Text);}catch(ExternalException){MessageBox.Show(this,"The clipboard is busy. Please try again.",Text);}};
            close.Click+=(s,e)=>Close();Shown+=(s,e)=>{details.Focus();details.Select(0,0);};RefreshLog();
        }
        void RefreshLog() {
            string text=App.ReadDiagnostics(folder);if(details.Text==text)return;
            int start=details.SelectionStart,length=details.SelectionLength;details.Text=text;
            details.Select(Math.Min(start,text.Length),Math.Min(length,Math.Max(0,text.Length-start)));
        }
    }

    public static class Installer {
        public static string Hash(Stream stream) { using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        public static void Validate(string root) {
            RejectLinks(root);
            var hashes=App.Json.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(root,"files.json")));
            if(hashes == null || hashes.Count < 110 || hashes.Count > 180) throw new InvalidDataException("Invalid package inventory.");
            string canonical=Path.GetFullPath(root).TrimEnd('\\')+"\\";
            foreach(var entry in hashes) {
                string path=SafePath(canonical,entry.Key);
                if(!File.Exists(path) || (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Missing or unsafe package file.");
                using(var file=File.OpenRead(path)) if(Hash(file)!=entry.Value) throw new InvalidDataException("A package file is damaged: "+entry.Key);
            }
            var actual=Directory.GetFiles(root,"*",SearchOption.AllDirectories).Select(p=>p.Substring(canonical.Length).Replace('\\','/')).Where(p=>p!="files.json").OrderBy(p=>p,StringComparer.Ordinal).ToArray();
            if(!actual.SequenceEqual(hashes.Keys.OrderBy(p=>p,StringComparer.Ordinal))) throw new InvalidDataException("The package contains unexpected files.");
            foreach(string required in new[]{"PhononSAPI.exe","PhononSAPI.exe.config","PhononHost.exe","PhononSAPI32.dll","PhononSAPI64.dll","voices.json","runtime/ptts_ffi.dll","runtime/libunwind.dll","model/config.json","model/tokenizer.json","model/model.safetensors"})
                if(!hashes.ContainsKey(required)) throw new InvalidDataException("Required package asset missing: "+required);
            foreach(var voice in App.Voices(root)) if(!hashes.ContainsKey("model/voices/"+voice.name+".safetensors")) throw new InvalidDataException("Voice data missing.");
        }
        static void RejectLinks(string folder) {
            if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("A package directory is redirected.");
            foreach(var child in Directory.GetDirectories(folder)) RejectLinks(child);
        }
        public static string SafePath(string root,string relative) {
            if(String.IsNullOrEmpty(relative) || relative.Contains('\\') || relative.Contains(':') || relative.StartsWith("/") || relative.Split('/').Any(x=>x==".." || x=="." || x.Length==0))
                throw new InvalidDataException("Unsafe package path.");
            string path=Path.GetFullPath(Path.Combine(root,relative.Replace('/','\\')));
            if(!path.StartsWith(Path.GetFullPath(root).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package path escapes its folder.");
            return path;
        }
    }

    public sealed class UpdateInfo {
        public string product; public string version; public string url; public string sha256;
        public long size; public string changes;
    }
    public sealed class Envelope { public string payload; public string signature; }
    public static class Updater {
        public const string Repo="OnjLouis/phononSAPI";
        // Verification only. Signing material is never distributed with this app.
        public const string Modulus="wcBm9EMsIbzawOOLc0a9HXZKVuGymcWePJlV2g611J7QiAc5PuZCebgmttAg+Iq2PI/+BX1bBRmnFeG89OLOmPosKxsrjYqpO2PtNoSTsAo+TPwlaNXntrrWDrnJNYdlugeypanNfTuAgir9yEE3WtKyENl5xZPQvwYweP1utOaUes95C0bxYNijumc+4dsRfxXRqxF5+TJw7QhX4MRC3Yid4TISyIz+M9rfVxJiVVQvDdbanoJ1m3qr7SKzXpHuLe0ZsKOnJ0fSYbFU78d0VZv7t748RzGuCYlleqQ4VVxTrVtF/0pszkwWmLhANY4hDOs3laWXOr4PLBlG2TgeKqomDocW9ko62jYA6LrFUdjjnEmm1KJeJKFshSlwCzcwD7lDiy0jV75ncRyzfGOxTYZ+xwd6Siys8p1EsfWot2BgjJTdocOV219ucD6tqfyudZ9olioHrZHSd95XV6u1ZNBOF+57ko0p5nTRXGWswznSl5VTUP+jxadMWLObLhBt";
        public static UpdateInfo Verify(string json) {
            return VerifyWithKey(json,new RSAParameters {Modulus=Convert.FromBase64String(Modulus),Exponent=new byte[]{1,0,1}});
        }
        internal static UpdateInfo VerifyWithKey(string json,RSAParameters verificationKey) {
            if(json.Length>128*1024) throw new InvalidDataException("Update information is too large.");
            Envelope envelope=App.Json.Deserialize<Envelope>(json);
            byte[] payload=Convert.FromBase64String(envelope.payload), signature=Convert.FromBase64String(envelope.signature);
            using(var rsa=new RSACryptoServiceProvider()) {
                rsa.PersistKeyInCsp=false;
                rsa.ImportParameters(verificationKey);
                if(!rsa.VerifyData(payload,CryptoConfig.MapNameToOID("SHA256"),signature)) throw new CryptographicException("The update signature is invalid.");
            }
            var info=App.Json.Deserialize<UpdateInfo>(new UTF8Encoding(false,true).GetString(payload));
            ValidateInfo(info);
            return info;
        }
        static void ValidateInfo(UpdateInfo info) {
            if(info==null) throw new InvalidDataException("Missing update metadata.");
            if(info.product!="phononSapi" || !System.Text.RegularExpressions.Regex.IsMatch(info.version??"",@"^\d+\.\d+\.\d+$") ||
                info.url!="https://github.com/"+Repo+"/releases/download/v"+info.version+"/Phonon-SAPI-"+info.version+".msi" ||
                info.size<=0 || info.size>256L*1024*1024 || !System.Text.RegularExpressions.Regex.IsMatch(info.sha256??"",@"^[0-9a-f]{64}$"))
                throw new InvalidDataException("Signed update metadata does not match Phonon SAPI.");
        }
        public static HttpClient Client() {
            var client=new HttpClient {Timeout=TimeSpan.FromMinutes(10),MaxResponseContentBufferSize=128*1024};
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PhononSAPI/"+App.Version); return client;
        }
        public static async Task<UpdateInfo> Check() {
            using(var client=Client()) {
                string json=await client.GetStringAsync("https://api.github.com/repos/"+Repo+"/releases/latest");
                var release=App.Json.Deserialize<Dictionary<string,object>>(json);
                string tag=Convert.ToString(release["tag_name"],CultureInfo.InvariantCulture);
                if(!System.Text.RegularExpressions.Regex.IsMatch(tag,@"^v\d+\.\d+\.\d+$")) throw new InvalidDataException("Invalid release version.");
                string manifest=await client.GetStringAsync("https://github.com/"+Repo+"/releases/download/"+tag+"/phononSapi-update.json");
                var info=Verify(manifest);
                if("v"+info.version!=tag) throw new InvalidDataException("Release version mismatch.");
                return new Version(info.version)>new Version(App.Version) ? info : null;
            }
        }
        public static async Task<string> Download(UpdateInfo info,CancellationToken cancellation) {
            using(var client=Client()) return await DownloadWithClient(info,cancellation,client);
        }
        internal static async Task<string> DownloadWithClient(UpdateInfo info,CancellationToken cancellation,HttpClient client) {
            ValidateInfo(info);
            string stage=Path.Combine(App.Data,"updates",Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try {
                string archive=Path.Combine(stage,"Phonon-SAPI-"+info.version+".msi");
                using(var response=await client.GetAsync(info.url,HttpCompletionOption.ResponseHeadersRead,cancellation)) {
                    response.EnsureSuccessStatusCode();
                    using(var input=await response.Content.ReadAsStreamAsync()) using(var output=new FileStream(archive,FileMode.CreateNew,FileAccess.Write)) {
                        byte[] block=new byte[65536]; long total=0; int count;
                        while((count=await input.ReadAsync(block,0,block.Length,cancellation))>0) {
                            total+=count; if(total>info.size) throw new InvalidDataException("Update exceeds signed size.");
                            await output.WriteAsync(block,0,count,cancellation);
                        }
                        if(total!=info.size) throw new InvalidDataException("Incomplete update download.");
                    }
                }
                using(var file=File.OpenRead(archive)) if(Installer.Hash(file)!=info.sha256) throw new CryptographicException("Update hash mismatch.");
                WindowsInstaller.Validate(archive,info.version);
                return archive;
            } catch { Directory.Delete(stage,true); throw; }
        }
        internal static void CleanOldDownloads(string root,DateTime now) {
            try {
                for(string path=Path.GetFullPath(root);path!=null;path=Path.GetDirectoryName(path)) {
                    if(!Directory.Exists(path))return;
                    if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)return;
                }
                foreach(string folder in Directory.EnumerateDirectories(root).Take(64)) {
                    try {
                        if(!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(folder),@"^[0-9a-f]{32}$") ||
                           (File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)continue;
                        var entries=Directory.EnumerateFileSystemEntries(folder).Take(2).ToArray();
                        if(entries.Length!=1)continue;
                        string file=entries[0];
                        if(!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file),@"^Phonon-SAPI-\d+\.\d+\.\d+\.msi$") ||
                           (File.GetAttributes(file)&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0 ||
                           now-File.GetLastWriteTimeUtc(file)<TimeSpan.FromHours(24))continue;
                        // An active installer may still hold the download; never force it closed.
                        using(File.Open(file,FileMode.Open,FileAccess.Read,FileShare.None)){}
                        File.Delete(file);Directory.Delete(folder,false);
                    }catch(IOException){}catch(UnauthorizedAccessException){}
                }
            }catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }

    public sealed class MainForm : Form {
        readonly Preferences preferences=App.LoadPreferences();
        readonly List<Voice> catalogue=App.Voices(App.Folder);
        readonly ComboBox languages=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList};
        readonly ListBox voices=new ListBox();
        readonly TextBox text=new TextBox {Multiline=true,ScrollBars=ScrollBars.Vertical,Text="Welcome to Phonon. These voices work with Windows speech applications."};
        readonly NumericUpDown rate=new NumericUpDown {Minimum=0,Maximum=100,Increment=5};
        readonly NumericUpDown pitch=new NumericUpDown {Minimum=0,Maximum=100,Increment=5};
        readonly NumericUpDown volume=new NumericUpDown {Minimum=0,Maximum=100,Value=100};
        readonly CheckBox gpu=new CheckBox {Text="Use &GPU acceleration",AutoSize=true};
        readonly ComboBox interval=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList};
        readonly Label status=new Label {AutoSize=true,Text="Ready",AccessibleName="Status"};
        readonly TextBox backend=new TextBox {ReadOnly=true,Text="Not initialized."};
        DiagnosticsDialog diagnosticsWindow;
        readonly Button play=new Button {Text="&Play"}, stop=new Button {Text="&Stop"}, check=new Button {Text="Check for &updates"};
        dynamic speech;
        bool checking;
        readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer {Interval=60000};
        readonly System.Windows.Forms.Timer backendTimer=new System.Windows.Forms.Timer {Interval=2000};
        readonly CancellationTokenSource closing=new CancellationTokenSource();
        public MainForm() {
            Updater.CleanOldDownloads(Path.Combine(App.Data,"updates"),DateTime.UtcNow);
            Text="Phonon SAPI "+App.Version; MinimumSize=new Size(640,660); Size=new Size(720,740);
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,Padding=new Padding(12),AutoScroll=true};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            Controls.Add(layout);
            var saved=catalogue.FirstOrDefault(v=>v.legacyId==preferences.Voice);if(saved!=null)preferences.Voice=saved.id;
            Add(layout,"&Language:",languages); languages.Items.Add(new LanguageChoice());
            languages.Items.AddRange(catalogue.Select(v=>v.locale).Distinct().Select(x=>new LanguageChoice{Locale=x}).Cast<object>().ToArray());
            Add(layout,"&Voice:",voices); voices.Height=160;
            Add(layout,"Te&xt:",text); text.Height=100;
            Add(layout,"&Rate:",rate); Add(layout,"Pi&tch:",pitch); Add(layout,"V&olume:",volume);
            rate.Value=preferences.Rate;
            pitch.Value=preferences.Pitch;
            volume.Value=Math.Max(0,Math.Min(100,preferences.Volume));
            gpu.Checked=preferences.Gpu; Add(layout,"",gpu);
            Add(layout,"Synthesis &backend:",backend);
            var buttons=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill}; buttons.Controls.Add(play); buttons.Controls.Add(stop); Add(layout,"",buttons);
            Add(layout,"Update &check:",interval); interval.Items.AddRange(new object[]{"Never","Hourly","Daily"});
            interval.SelectedIndex=preferences.Interval=="never"?0:preferences.Interval=="hourly"?1:2;
            Add(layout,"",check);
            var maintenance=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill};
            var install=new Button {Text="Repair &installation",AutoSize=true}; var remove=new Button {Text="Re&move application",AutoSize=true}; var help=new Button {Text="&Help"};
            var diagnostics=new Button {Text="&Diagnostics",AutoSize=true};
            maintenance.Controls.AddRange(new Control[]{install,remove,help,diagnostics}); Add(layout,"",maintenance);
            var websites=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill};
            var software=new Button {Text="Soft&ware catalogue",AutoSize=true,Tag=App.SoftwareUrl};
            var donate=new Button {Text="Do&nate",AutoSize=true,Tag=App.DonationUrl};
            websites.Controls.AddRange(new Control[]{software,donate});Add(layout,"",websites);Add(layout,"",status);
            software.Click+=(s,e)=>OpenWebsite(App.SoftwareUrl);donate.Click+=(s,e)=>OpenWebsite(App.DonationUrl);
            languages.SelectedIndexChanged+=(s,e)=>RefreshVoices(); languages.SelectedIndex=0;
            gpu.CheckedChanged+=(s,e)=>Save(); interval.SelectedIndexChanged+=(s,e)=>Save();
            rate.ValueChanged+=(s,e)=>Save(); pitch.ValueChanged+=(s,e)=>Save(); volume.ValueChanged+=(s,e)=>Save();
            voices.SelectedIndexChanged+=(s,e)=>Save();
            play.Click+=(s,e)=>Play(); stop.Click+=(s,e)=>Stop();
            install.Click+=(s,e)=>Maintenance(false); remove.Click+=(s,e)=>Maintenance(true);
            help.Click+=(s,e)=>Process.Start(new ProcessStartInfo(Path.Combine(App.Folder,"Manual.html")){UseShellExecute=true});
            diagnostics.Click+=(s,e)=>{if(diagnosticsWindow==null || diagnosticsWindow.IsDisposed)diagnosticsWindow=new DiagnosticsDialog(App.Data);diagnosticsWindow.Show(this);diagnosticsWindow.BringToFront();};
            check.Click+=async(s,e)=>await Check(true);
            timer.Tick+=async(s,e)=>await Check(false); Shown+=async(s,e)=>{timer.Start();backendTimer.Start();RefreshBackend();await Check(false);};
            backendTimer.Tick+=(s,e)=>RefreshBackend();
            FormClosing+=(s,e)=>{closing.Cancel();timer.Stop();backendTimer.Stop();Stop();Save();if(diagnosticsWindow!=null)diagnosticsWindow.Close();if(speech!=null) Marshal.FinalReleaseComObject(speech);};
            FormClosed+=(s,e)=>{timer.Dispose();backendTimer.Dispose();closing.Dispose();};
        }
        static void Add(TableLayoutPanel panel,string label,Control control) {
            int row=panel.RowCount++; panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if(label.Length>0) { var name=new Label {Text=label,AutoSize=true,Anchor=AnchorStyles.Left}; panel.Controls.Add(name,0,row); control.AccessibleName=label.Replace("&","").TrimEnd(':'); }
            control.Dock=DockStyle.Fill; control.Margin=new Padding(4,4,4,8); panel.Controls.Add(control,1,row);
        }
        void RefreshVoices() {
            var selected=voices.SelectedItem as Voice;
            string name=selected==null?preferences.Voice:selected.id;
            voices.BeginUpdate(); voices.Items.Clear();
            var language=languages.SelectedItem as LanguageChoice;
            voices.Items.AddRange(catalogue.Where(v=>language==null || language.Locale==null || v.locale==language.Locale).Cast<object>().ToArray());
            voices.SelectedItem=voices.Items.Cast<Voice>().FirstOrDefault(v=>v.id==name);
            if(voices.SelectedIndex<0 && voices.Items.Count>0) voices.SelectedIndex=0;
            voices.EndUpdate();
        }
        void RefreshBackend() {string value=BackendStatus.Current(gpu.Checked);if(backend.Text!=value)backend.Text=value;}
        void OpenWebsite(string url) {
            try {Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}
            catch(Exception error) {App.Log(error.Message);MessageBox.Show(this,"The website could not be opened in your default browser.","Phonon SAPI",MessageBoxButtons.OK,MessageBoxIcon.Error);}
        }
        void Save() {
            preferences.Gpu=gpu.Checked; preferences.Interval=new[]{"never","hourly","daily"}[Math.Max(0,interval.SelectedIndex)];
            preferences.Rate=(int)rate.Value;preferences.Pitch=(int)pitch.Value;preferences.Volume=(int)volume.Value;
            var selected=voices.SelectedItem as Voice; if(selected!=null) preferences.Voice=selected.id;
            try {App.Save(preferences);} catch(Exception error) {App.Log(error.Message);}
        }
        void Play() {
            try {
                Stop(); var selected=voices.SelectedItem as Voice; if(selected==null || String.IsNullOrWhiteSpace(text.Text)) return;
                if(speech==null) speech=Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice"));
                dynamic available=speech.GetVoices("",""); dynamic chosen=null;
                for(int i=0;i<(int)available.Count;i++) {dynamic item=available.Item(i);if(((string)item.Id).EndsWith("\\"+selected.id,StringComparison.OrdinalIgnoreCase)){chosen=item;break;}Marshal.FinalReleaseComObject(item);}
                if(chosen==null) {Marshal.FinalReleaseComObject(available);throw new InvalidOperationException("Install the SAPI voices first, then reopen your speech application.");}
                speech.Voice=chosen; Marshal.FinalReleaseComObject(chosen); Marshal.FinalReleaseComObject(available);
                speech.Rate=App.ToSapiControl((int)rate.Value); speech.Volume=(int)volume.Value;
                string input="<pitch absmiddle='"+App.ToSapiControl((int)pitch.Value).ToString(CultureInfo.InvariantCulture)+"'>"+System.Security.SecurityElement.Escape(text.Text)+"</pitch>";
                speech.Speak(input,9); status.Text="Speaking";
            } catch(Exception error) {MessageBox.Show(this,error.Message,"Phonon SAPI",MessageBoxButtons.OK,MessageBoxIcon.Error);}
        }
        void Stop() { try {if(speech!=null) speech.Speak("",3);status.Text="Ready";}catch(Exception error){App.Log(error.Message);} }
        void Maintenance(bool remove) {
            Stop();
            if(MessageBox.Show(this,remove?"Remove Phonon SAPI and its voice registrations? Your saved preferences will be kept. This settings window will close.":"Repair the Phonon SAPI installation? This settings window will close. Close other applications using these voices first.","Phonon SAPI",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
            try{WindowsInstaller.Maintenance(remove);Close();}catch(Exception error){MessageBox.Show(this,error.Message,"Phonon SAPI");}
        }
        async Task Check(bool manual) {
            if(checking || closing.IsCancellationRequested || (!manual && preferences.Interval=="never")) return;
            double hours=preferences.Interval=="hourly"?1:24;
            if(!manual && (DateTime.UtcNow-preferences.LastCheck).TotalHours<hours) return;
            checking=true;check.Enabled=false;
            preferences.LastCheck=DateTime.UtcNow;Save();
            try {
                status.Text="Checking for updates..."; var update=await Updater.Check();
                if(closing.IsCancellationRequested) return;
                status.Text=update==null?"Phonon SAPI is up to date.":"Update available: "+update.version;
                if(update==null) return;
                if(MessageBox.Show(this,"Install Phonon SAPI "+update.version+"?\n\n"+update.changes+"\n\nClose applications using these voices first. Administrator permission will be requested, then this settings window will close. Reopen it from the Start menu afterward. No applications will be forcibly closed.","Phonon SAPI update",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
                status.Text="Downloading and verifying update...";
                string installer=await Updater.Download(update,closing.Token);
                WindowsInstaller.Install(installer);
                Close();
            } catch(OperationCanceledException) {} catch(Exception error) {App.Log(error.ToString());status.Text="Update check failed. See the diagnostic log.";if(manual)MessageBox.Show(this,error.Message,"Phonon SAPI updates");}
            finally {checking=false;if(!IsDisposed)check.Enabled=true;}
        }
    }
}
