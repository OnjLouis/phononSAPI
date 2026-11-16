// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Forms;

namespace PhononSapi {
    internal static class UpdateInteractionTests {
        static void Require(bool condition,string message) {if(!condition)throw new Exception(message);}

        public sealed class TaskFolderFixture {
            internal Exception Failure;
            public object GetTask(string name) {if(Failure!=null)throw Failure;return new object();}
        }

        static void TestMissingTask() {
            var method=typeof(RestartUpdate).GetMethod("TaskExists",BindingFlags.Static|BindingFlags.NonPublic);
            var folder=new TaskFolderFixture();
            Func<bool> exists=()=>(bool)method.Invoke(null,new object[]{folder});
            Require(exists(),"An existing update task must be detected");
            const int missing=unchecked((int)0x80070002);
            folder.Failure=new COMException("Task not found",missing);
            Require(!exists(),"A missing task reported as a COM exception must not block scheduling");
            folder.Failure=Marshal.GetExceptionForHR(missing,new IntPtr(-1));
            Require(folder.Failure is FileNotFoundException && !exists(),"A missing task mapped to FileNotFoundException must not block scheduling");
            folder.Failure=new UnauthorizedAccessException("Task access denied");
            try {exists();throw new Exception("Task access denied must not be treated as missing");}
            catch(TargetInvocationException error) {Require(error.InnerException==folder.Failure,"Unexpected task scheduler error");}
        }
        static IEnumerable<T> Children<T>(Control parent) where T:Control {
            foreach(Control child in parent.Controls) {
                if(child is T)yield return (T)child;
                foreach(var descendant in Children<T>(child))yield return descendant;
            }
        }
        delegate int InstallerMessage(IntPtr context,uint type,[MarshalAs(UnmanagedType.LPWStr)] string text);
        [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiOpenPackageEx(string path,uint options,out uint handle);
        [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiSetProperty(uint handle,string name,string value);
        [DllImport("prerequisites.dll")] static extern uint CheckPhononUsage(uint handle);
        [DllImport("msi.dll")] static extern uint MsiCloseHandle(uint handle);
        [DllImport("msi.dll")] static extern uint MsiSetInternalUI(uint level,IntPtr window);
        [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern IntPtr MsiSetExternalUI(InstallerMessage handler,uint filter,IntPtr context);
        [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr window,uint objectId,ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out object accessible);

        // Invoke the real installer guard without installing, upgrading, or registering anything.
        static List<string> NativeGuard(string fixture,string root,bool success) {
            uint handle;
            var messages=new List<string>();
            InstallerMessage receive=(context,type,text)=>{messages.Add(text);return 1;};
            uint previous=MsiSetInternalUI(2,IntPtr.Zero);
            MsiSetExternalUI(receive,2,IntPtr.Zero);
            try {
                Require(MsiOpenPackageEx(fixture,1,out handle)==0,"Could not open isolated MSI");
                try {
                    Require(MsiSetProperty(handle,"INSTALLFOLDER",root)==0,"Could not point the guard at its fixture");
                    uint result=CheckPhononUsage(handle);
                    messages.Add("Guard result: "+result);
                    Require(success?result==0:result==1603,"Unexpected result from MSI lock guard: "+result+" "+String.Join("; ",messages));
                } finally{MsiCloseHandle(handle);}
            } finally{MsiSetExternalUI(null,0,IntPtr.Zero);MsiSetInternalUI(previous,IntPtr.Zero);}
            return messages;
        }

        static string InstallerLog(string fixture,string root,string name) {
            string path=Path.Combine(root,name+".log");
            string msi=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"msiexec.exe");
            using(var process=Process.Start(new ProcessStartInfo(msi,"/i \""+fixture+"\" INSTALLFOLDER=\""+root+"\" /qn /norestart /l*v \""+path+"\"") {
                UseShellExecute=false,CreateNoWindow=true
            })) {
                Require(process.WaitForExit(20000),"The isolated MSI did not exit");
                Require(process.ExitCode==1603,"The isolated MSI should always stop before install");
            }
            return File.ReadAllText(path);
        }

        [STAThread]
        public static int Main(string[] args) {
            if(args.Length==1 && args[0]=="--task-lookup") {TestMissingTask();Console.WriteLine("PASS: missing-task HRESULT and fail-closed access errors");return 0;}
            if(args.Length==1 && args[0]=="--hold") {
                Console.WriteLine("ready");
                return Console.ReadLine()=="exit"?0:1;
            }
            TestMissingTask();
            string root=Path.GetFullPath(args[0]);
            Directory.CreateDirectory(root);
            var method=typeof(WindowsInstaller).GetMethod("FindBlockers",BindingFlags.Static|BindingFlags.NonPublic);
            Require(method!=null,"Missing app-side Restart Manager check");
            Func<int,string[]> blockers=pid=>(string[])method.Invoke(null,new object[]{root,pid});
            Require(blockers(0).Length==0,"An empty folder should not report a blocker");
            string image=Path.Combine(root,"PhononHost.exe");
            File.Copy(Assembly.GetExecutingAssembly().Location,image);
            using(var holder=Process.Start(new ProcessStartInfo(image,"--hold") {
                UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,CreateNoWindow=true
            })) {
                try {
                    Require(holder.StandardOutput.ReadLine()=="ready","Lock fixture did not start");
                    string[] names=blockers(0);
                    Require(names.Length==1 && names[0].Length>0 && !names[0].Contains(holder.Id.ToString()),"The real lock holder should be named without a process ID");
                    var users=WindowsInstaller.FindResourceUsers(root,0);
                    Require(!WindowsInstaller.CanRestart(users),"An unregistered process must not be eligible for automatic closure");
                    Require(CooperativeUpdate.Approval(users)!=CooperativeUpdate.Approval(new WindowsInstaller.ResourceUser[0]),"Approval must be tied to the exact process instance");
                    Require(blockers(holder.Id).Length==0,"The settings process should be excluded from its own preflight");
                    if(args.Length>1) {
                        string message=String.Join("\n",NativeGuard(args[1],root,false));
                        Require(message.Contains(names[0]) && message.Contains("applications are using Phonon") && !message.Contains("(process "),
                            "The MSI did not show the fixture's app name without a process ID: "+message);
                        string log=InstallerLog(args[1],root,"blocked");
                        Require(log.Contains(names[0]) && !log.Contains("(process "+holder.Id+")"),"The installer should report the app name without its process ID");
                        Require(!holder.HasExited,"The MSI closed an application without permission");
                    }
                } finally {holder.StandardInput.WriteLine("exit");Require(holder.WaitForExit(5000),"Lock fixture did not exit");}
            }
            using(var dialog=UpdatePreflight.CreateRestartChoices("Eligible fixture app",true)) {
                var buttons=new List<Button>(Children<Button>(dialog));
                var close=buttons.Find(b=>b.DialogResult==DialogResult.OK);
                Require(buttons.Count==4 && close!=null && Control.IsMnemonic('C',close.Text) &&
                    buttons.FindAll(b=>Control.IsMnemonic('C',b.Text)).Count==1 && dialog.CancelButton!=close,
                    "Close apps and update must be an explicit, uniquely accessible choice");
            }
            Require(blockers(0).Length==0,"An exited app should no longer block Retry");
            if(args.Length>1) {
                NativeGuard(args[1],root,true);
                string log=InstallerLog(args[1],root,"clear");
                Require(log.Contains("Guard fixture deliberately stopped before installation."),"The clear path did not reach the fixture stop");
                Console.WriteLine("PASS: real MSI guard names the blocker, leaves it running and succeeds after exit");
            }
            var shortcut=typeof(MainForm).GetMethod("ProcessCmdKey",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.DeclaredOnly);
            Require(shortcut!=null,"Missing Shift+F1 shortcut in the settings window");
            int checks=0;
            Action check=()=>checks++;
            Require(MainForm.HandleUpdateShortcut(Keys.Shift|Keys.F1,check) && checks==1,"Shift+F1 did not invoke the update check");
            Require(!MainForm.HandleUpdateShortcut(Keys.F1,check) &&
                !MainForm.HandleUpdateShortcut(Keys.Control|Keys.Shift|Keys.F1,check) && checks==1,"Other keys must keep their normal behavior");
            int plays=0,resets=0;
            Require(MainForm.HandlePreviewShortcut(Keys.Control|Keys.T,()=>plays++,()=>resets++) && plays==1 && resets==0,
                "Ctrl+T must play");
            Require(MainForm.HandlePreviewShortcut(Keys.Control|Keys.R,()=>plays++,()=>resets++) && plays==1 && resets==1,
                "Ctrl+R must reset");
            Require(!MainForm.HandlePreviewShortcut(Keys.Control|Keys.Shift|Keys.R,()=>plays++,()=>resets++) &&
                !MainForm.HandlePreviewShortcut(Keys.T,()=>plays++,()=>resets++),"Unrelated shortcuts must pass through");
            Type buttonType=typeof(MainForm).Assembly.GetType("PhononSapi.UpdateCheckButton");
            Require(buttonType!=null,"Update button does not expose its keyboard shortcut");
            using(var button=(Button)Activator.CreateInstance(buttonType,true)) {
                Require(button.Text=="Check for updates","The update button should not claim a second shortcut");
                var accessible=button.AccessibilityObject;
                Require(accessible.Name=="Check for updates" && accessible.Role==AccessibleRole.PushButton,"Accessible button name or role changed");
                Require(accessible.KeyboardShortcut=="Shift+F1","Shift+F1 must be exposed separately from the button name");
                // Query the HWND through MSAA, not just the managed accessibility object.
                var iid=new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
                object client;
                Require(AccessibleObjectFromWindow(button.Handle,unchecked((uint)-4),ref iid,out client)==0,"Windows could not read the button's MSAA object");
                dynamic msaa=client;
                Require((string)msaa.accKeyboardShortcut[0]=="Shift+F1","Windows MSAA did not report Shift+F1");
            }
            using(var dialog=UpdatePreflight.CreateRestartChoices("Fixture process")) {
                var buttons=new List<Button>(Children<Button>(dialog));
                Require(buttons.Count==3 && dialog.CancelButton==buttons[2],"The dialog should expose Retry, Install and Escape to cancel");
                var retry=buttons.Find(b=>b.DialogResult==DialogResult.Retry);
                var install=buttons.Find(b=>b.DialogResult==DialogResult.Yes);
                Require(retry!=null && Control.IsMnemonic('R',retry.Text) && buttons.FindAll(b=>Control.IsMnemonic('R',b.Text)).Count==1,
                    "Retry needs the unique Alt+R mnemonic");
                Require(install!=null && Control.IsMnemonic('I',install.Text) && buttons.FindAll(b=>Control.IsMnemonic('I',b.Text)).Count==1,
                    "Install at next restart needs the unique Alt+I mnemonic");
                var retryShortcut=retry.AccessibilityObject.KeyboardShortcut;
                var installShortcut=install.AccessibilityObject.KeyboardShortcut;
                Require(String.Equals(retryShortcut,"Alt+R",StringComparison.OrdinalIgnoreCase) && String.Equals(installShortcut,"Alt+I",StringComparison.OrdinalIgnoreCase),
                    "Windows must expose both mnemonics: Retry="+retryShortcut+", Install="+installShortcut);
                Require(buttons[0].AccessibilityObject.Name=="Retry" && buttons[1].AccessibilityObject.Name=="Install at next restart",
                    "Update action names should not include shortcut text");
            }
            string download=Path.Combine(root,"PhononSAPI","updates",new string('a',32));
            Directory.CreateDirectory(download);
            Require(RestartUpdate.ValidateDownloadFolder(download)==download,"A normal signed download folder should be allowed");
            try {RestartUpdate.ValidateDownloadFolder(root);throw new Exception("A non-update folder was accepted");} catch(SecurityException){}
            using(var package=new MemoryStream(new byte[]{1,2,3})) {
                var info=new UpdateInfo {size=package.Length,sha256=Installer.Hash(package)};
                package.Position=0;RestartUpdate.VerifyPackage(package,info);
                package.Position=0;package.WriteByte(2);package.Position=0;
                try {RestartUpdate.VerifyPackage(package,info);throw new Exception("A changed update package was accepted");} catch(SecurityException){}
            }
            dynamic scheduler=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",true));scheduler.Connect();
            dynamic task=RestartUpdate.CreateTaskDefinition(scheduler,root,DateTimeOffset.Now.AddDays(7));
            Require((int)task.Triggers.Count==1 && (int)task.Triggers.Item(1).Type==8 && !(bool)task.Settings.AllowDemandStart,
                "The update task must only be eligible at boot");
            Require((string)task.Principal.UserId=="SYSTEM" && (int)task.Principal.LogonType==5 && (int)task.Actions.Count==1,
                "The startup task must not depend on an interactive user");
            dynamic action=task.Actions.Item(1);
            Require((string)action.Path==Path.Combine(root,"PhononUpdate.exe") && (string)action.Arguments==RestartUpdate.ApplyOption &&
                (string)action.WorkingDirectory==root,"Task action must target only the staged updater");
            if(args.Length>3) {
                string signed=File.ReadAllText(args[2]);
                UpdateInfo update=Updater.Verify(signed);
                Require(update.SignedManifest==signed,"The updater must carry the signed envelope, not reconstructed metadata");
                using(var package=File.OpenRead(args[3]))RestartUpdate.VerifyPackage(package,update);
                WindowsInstaller.Validate(args[3],update.version);
                var envelope=App.Json.Deserialize<Envelope>(signed);
                byte[] signature=Convert.FromBase64String(envelope.signature);signature[0]^=1;
                envelope.signature=Convert.ToBase64String(signature);
                try {Updater.Verify(App.Json.Serialize(envelope));throw new Exception("A modified update signature was accepted");}
                catch(System.Security.Cryptography.CryptographicException){}
                Console.WriteLine("PASS: real published envelope, package identity and tampered-signature rejection");
            }
            Console.WriteLine("PASS: signed-package size/hash check, staging path rejection and actual Windows boot task definition (not registered)");
            Console.WriteLine("PASS: empty, locked, ignored and released Windows resources; Shift+F1 dispatch and MSAA metadata");
            return 0;
        }
    }
}
