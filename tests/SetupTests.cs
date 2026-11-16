// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace PhononSapi {
    internal static class SetupTests {
        static void Require(bool value,string message){if(!value)throw new Exception(message);}
        static T ControlOf<T>(Form form,string name) where T:Control {
            return (T)typeof(SetupForm).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);
        }
        static void VerifyConsent() {
            using(var form=new SetupForm()) {
                var install=ControlOf<Button>(form,"install");
                var cancel=ControlOf<Button>(form,"cancel");
                Require(form.AcceptButton==null && form.CancelButton==cancel,"Confirmation must not default to installing.");
                Require(Control.IsMnemonic('I',install.Text) && Control.IsMnemonic('C',cancel.Text),"Confirmation mnemonics.");
                Require(ControlOf<TextBox>(form,"details").Text.Contains("Cancel to leave everything unchanged"),"Explain cancellation.");
            }
            using(var form=UpdatePreflight.CreateRestartChoices("App is using Phonon",true,false,true)) {
                var buttons=form.Controls[0].Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().ToArray();
                Require(buttons.Length==3 && buttons.Any(b=>b.DialogResult==DialogResult.OK) &&
                    buttons.All(b=>b.DialogResult!=DialogResult.Yes),"Unsigned setup cannot queue privileged startup work.");
            }
            using(var play=new PreviewButton("Play","Ctrl+T"))
            using(var reset=new PreviewButton("Reset Sample","Ctrl+R")) {
                Require(play.AccessibilityObject.Name=="Play" && play.AccessibilityObject.KeyboardShortcut=="Ctrl+T","Play exposes the shortcut separately.");
                Require(reset.AccessibilityObject.Name=="Reset Sample" && reset.AccessibilityObject.KeyboardShortcut=="Ctrl+R","Reset exposes the shortcut separately.");
            }
        }
        [STAThread]
        public static int Main(string[] args) {
            if(args.Length==1 && args[0]=="--hold"){Console.WriteLine("ready");return Console.ReadLine()=="exit"?0:1;}
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            VerifyConsent();
            int played=0,resetCount=0;
            Require(MainForm.HandlePreviewShortcut(Keys.Control|Keys.T,()=>played++,()=>resetCount++) && played==1,"Ctrl+T");
            Require(MainForm.HandlePreviewShortcut(Keys.Control|Keys.R,()=>played++,()=>resetCount++) && resetCount==1,"Ctrl+R");
            bool guest=args.Length==1 && args[0]=="--install-in-lab";
            if(guest) {
                Require(Environment.MachineName=="CODEX-WINLAB","Machine installation tests require the Windows lab.");
                // Guest Operations does not necessarily load the interactive Windows profile.
                typeof(App).GetField("Data").SetValue(null,Path.Combine(App.Folder,"guest-profile","PhononSAPI"));
            }
            else {
                Require(args.Length==2 && args[0]=="--isolated","Pass an isolated test directory.");
                typeof(App).GetField("Data").SetValue(null,Path.Combine(Path.GetFullPath(args[1]),"PhononSAPI"));
            }
            string package;
            using(var payload=new SetupPayload()) {
                package=payload.Package;
                Require(payload.Update!=null && payload.Update.version==App.Version,"Signed update information must match setup.");
                using(var stream=payload.OpenVerified())RestartUpdate.VerifyPackage(stream,payload.Update);
                if(!guest) {
                    using(var stream=new FileStream(package,FileMode.Open,FileAccess.Write))stream.WriteByte(0);
                    bool rejected=false;
                    try{using(payload.OpenVerified()){}}
                    catch(InvalidDataException){rejected=true;}
                    Require(rejected,"Setup must refuse a modified staged package before elevation.");
                }
                if(guest) {
                    string blocker=Path.Combine(App.InstallRoot,"PhononHost.exe");
                    if(!Directory.Exists(App.InstallRoot))Directory.CreateDirectory(App.InstallRoot);
                    Require(!File.Exists(blocker),"Lock fixture must not replace an installed application.");
                    File.Copy(Assembly.GetExecutingAssembly().Location,blocker);
                    try {
                        using(var process=Process.Start(new ProcessStartInfo(blocker,"--hold"){
                            UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,CreateNoWindow=true})) {
                            try {
                                Require(process.StandardOutput.ReadLine()=="ready","Lock holder failed.");
                                var users=WindowsInstaller.FindResourceUsers(App.InstallRoot,0);
                                Require(users.Any(user=>user.ProcessId==process.Id) && !WindowsInstaller.CanRestart(users),"Unregistered app must not be closed.");
                                bool refused=false;
                                try{SetupForm.RunInstaller(payload,null);}catch(IOException){refused=true;}
                                Require(refused && !process.HasExited && File.Exists(package),"Blocked install must keep the app running and preserve the package for retry.");
                            } finally {process.StandardInput.WriteLine("exit");Require(process.WaitForExit(5000),"Lock holder did not exit.");}
                        }
                    } finally {File.Delete(blocker);}
                    Require(SetupForm.RunInstaller(payload,null)==0,"Retry using the same package must succeed.");
                    Require(WindowsInstaller.InstalledVersion()==new Version(App.Version),"Installed MSI version did not match.");
                }
            }
            Require(!File.Exists(package),"Setup must remove its temporary package.");
            Console.WriteLine("Setup consent, keyboard/focus, signed package and cleanup passed."+
                (guest?" The real MSI rejected a live lock, preserved the package and installed after retry.":""));
            return 0;
        }
    }
}
