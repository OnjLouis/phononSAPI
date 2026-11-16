// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PhononSapi {
    internal static class Setup {
        [STAThread]
        public static int Main(string[] args) {
            if(args.Length!=0)return 2;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using(var form=new SetupForm())Application.Run(form);
            return 0;
        }

        internal static Stream Resource(string name) {return Assembly.GetExecutingAssembly().GetManifestResourceStream(name);}
    }

    internal sealed class SetupPayload : IDisposable {
        const string PackageResource="PhononSetup.msi", HashResource="PhononSetup.sha256", ManifestResource="PhononSetup.update.json";
        readonly string folder, expectedHash;
        internal readonly string Package;
        internal readonly UpdateInfo Update;

        internal SetupPayload() {
            using(var hash=Setup.Resource(HashResource)) {
                if(hash==null)throw new InvalidDataException("This setup is missing its package verification data.");
                using(var reader=new StreamReader(hash))expectedHash=reader.ReadToEnd().Trim();
            }
            if(!Regex.IsMatch(expectedHash,@"\A[0-9a-f]{64}\z"))throw new InvalidDataException("The package verification data is invalid.");
            using(var manifest=Setup.Resource(ManifestResource)) {
                if(manifest!=null)using(var reader=new StreamReader(manifest)) {
                    Update=Updater.Verify(reader.ReadToEnd());
                    if(Update.version!=App.Version || Update.sha256!=expectedHash)
                        throw new InvalidDataException("The signed package information does not match setup.");
                }
            }
            folder=Path.Combine(App.Data,"updates",Guid.NewGuid().ToString("N"));
            Package=Path.Combine(folder,"Phonon-SAPI-"+App.Version+".msi");
            try {
                Directory.CreateDirectory(folder);
                RestartUpdate.ValidateDownloadFolder(folder);
                using(var input=Setup.Resource(PackageResource)) {
                    if(input==null)throw new InvalidDataException("This setup is missing the installer package.");
                    using(var output=new FileStream(Package,FileMode.CreateNew,FileAccess.Write,FileShare.None))input.CopyTo(output);
                }
                using(OpenVerified()) {}
                WindowsInstaller.Validate(Package,App.Version);
            } catch {
                Dispose();
                throw;
            }
        }

        internal FileStream OpenVerified() {
            RestartUpdate.ValidateDownloadFolder(folder);
            if((File.GetAttributes(Package)&FileAttributes.ReparsePoint)!=0)throw new IOException("The installer package was redirected.");
            var input=new FileStream(Package,FileMode.Open,FileAccess.Read,FileShare.Read);
            try {
                if(Installer.Hash(input)!=expectedHash)throw new InvalidDataException("The installer package failed verification. Download a fresh copy of setup.");
                input.Position=0;
                return input;
            } catch {input.Dispose();throw;}
        }

        public void Dispose() {
            if(!Directory.Exists(folder))return;
            try {
                RestartUpdate.ValidateDownloadFolder(folder);
                if(File.Exists(Package) && (File.GetAttributes(Package)&FileAttributes.ReparsePoint)==0)File.Delete(Package);
                if(Directory.GetFileSystemEntries(folder).Length==0)Directory.Delete(folder);
            } catch(IOException error){App.Log(error.Message);}
              catch(UnauthorizedAccessException error){App.Log(error.Message);}
              catch(System.Security.SecurityException error){App.Log(error.Message);}
        }
    }

    internal sealed class SetupForm : Form {
        readonly Button install=new Button {Text="&Install",AutoSize=true};
        readonly Button cancel=new Button {Text="&Cancel",AutoSize=true};
        readonly TextBox details=new TextBox {ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,
            AccessibleName="Phonon SAPI setup status"};
        SetupPayload payload;
        bool busy,finished;
        const int UserCancelled=1602, RestartRequired=3010, ElevationDeclined=1223;

        internal SetupForm() {
            Text="Phonon SAPI "+App.Version+" Setup";
            StartPosition=FormStartPosition.CenterScreen;
            ClientSize=new Size(560,245);FormBorderStyle=FormBorderStyle.FixedDialog;
            MinimizeBox=false;MaximizeBox=false;
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Padding=new Padding(12)};
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var buttons=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill};
            buttons.Controls.AddRange(new Control[]{install,cancel});
            layout.Controls.Add(details,0,0);layout.Controls.Add(buttons,0,1);Controls.Add(layout);
            CancelButton=cancel;
            cancel.Click+=(s,e)=>Close();
            install.Click+=async(s,e)=>await Install();
            Shown+=(s,e)=>details.Focus();
            FormClosing+=(s,e)=>{if(busy)e.Cancel=true;};
            Status("Install Phonon SAPI "+App.Version+"? This adds speech voices for 32-bit and 64-bit apps. "+
                "Your default voice and preferences will not change. Choose Install to continue or Cancel to leave everything unchanged.");
        }

        void Status(string text) {details.Text=text;details.Select(0,0);}

        async Task Install() {
            if(busy || finished)return;
            busy=true;install.Enabled=false;cancel.Enabled=false;
            try {
                Version installed=null;
                try{installed=WindowsInstaller.InstalledVersion();}catch(InvalidOperationException){}
                if(installed!=null && installed>=new Version(App.Version)) {
                    Finish(installed==new Version(App.Version)?"This version of Phonon SAPI is already installed.":"A newer version of Phonon SAPI is already installed.");
                    return;
                }
                if(payload==null) {
                    Status("Verifying the installer package...");
                    payload=await Task.Run(()=>new SetupPayload());
                }
                WindowsInstaller.ResourceUser[] approved=null;
                var choice=await UpdatePreflight.Check(this,App.InstallRoot,Status,App.Log,CancellationToken.None,
                    installed!=null && payload.Update!=null,users=>approved=users,true);
                if(choice==UpdateChoice.Cancel) {Status("Setup cancelled. No changes were made. You can retry with this same installer.");return;}
                if(choice==UpdateChoice.InstallAtRestart) {
                    await Task.Run(()=>RestartUpdate.Request(payload.Package,payload.Update));
                    Finish("The update is ready. Restart Windows when convenient. Setup did not close any apps.");
                    return;
                }
                Status("Waiting for Windows Installer. Administrator approval may be required.");
                int result=await Task.Run(()=>RunInstaller(payload,approved));
                if(result==UserCancelled)Status("Installation cancelled. Choose Install to retry, or Cancel to exit.");
                else if(result==0 || result==RestartRequired)
                    Finish(result==0?"Phonon SAPI was installed. Open it from the Start menu.":"Phonon SAPI was installed. Restart Windows when ready to finish.");
                else throw new IOException("Windows Installer did not finish (code "+result+"). Close any apps using Phonon and choose Install to retry. No new download is needed.");
            } catch(Win32Exception error) {
                if(error.NativeErrorCode==ElevationDeclined)Status("Administrator approval was cancelled. You can retry with this same installer.");
                else Failed(error);
            } catch(Exception error){Failed(error);}
            finally {
                busy=false;install.Enabled=!finished;cancel.Enabled=true;
                details.Focus();
            }
        }

        internal static int RunInstaller(SetupPayload payload,WindowsInstaller.ResourceUser[] approved) {
            int result=0;
            using(payload.OpenVerified()) {
                Action run=()=>{
                    var late=WindowsInstaller.FindBlockers(App.InstallRoot,0);
                    if(late.Length!=0)throw new IOException("These apps are still using Phonon: "+String.Join(", ",late)+". Choose Install to check again.");
                    result=WindowsInstaller.InstallAndWait(payload.Package,true);
                    if(result!=0 && result!=RestartRequired && result!=UserCancelled)
                        throw new IOException("Windows Installer did not finish (code "+result+"). Choose Install to retry.");
                };
                if(approved==null)run();
                else {
                    var current=WindowsInstaller.FindResourceUsers(App.InstallRoot,0);
                    if(CooperativeUpdate.Approval(current)!=CooperativeUpdate.Approval(approved))
                        throw new IOException("The apps using Phonon changed. No apps were closed. Choose Install to check again.");
                    WindowsInstaller.CloseAndRestore(current,run);
                }
            }
            return result;
        }

        void Finish(string message){finished=true;cancel.Text="&Close";Status(message);}
        void Failed(Exception error){App.Log(error.ToString());Status(error.Message);}
        protected override void Dispose(bool disposing){if(disposing && payload!=null)payload.Dispose();base.Dispose(disposing);}
    }
}
