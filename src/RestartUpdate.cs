// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;

namespace PhononSapi {
    internal static class RestartUpdate {
        internal const string QueueOption="--queue-restart-update", ApplyOption="--apply-restart-update", CancelOption="--cancel-restart-update";
        const string TaskName="Phonon SAPI - install pending update", Runner="PhononUpdate.exe", Manifest="phononSapi-update.json",
            Package="Pending.msi", Attempt="Attempt", Result="Result.txt", StateLock="State.lock";
        internal const string PendingNotice="A Phonon SAPI update is scheduled. Restart Windows when you are ready.";
        const string AdminSid="S-1-5-32-544", SystemSid="S-1-5-18", UsersSid="S-1-5-32-545";
        const string StageAcl="O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FRFX;;;BU)";
        const string TaskAcl="D:P(A;;GA;;;SY)(A;;GA;;;BA)";
        const int TaskCreate=2, ServiceAccountLogon=5, BootTrigger=8, ExecuteAction=0;
        const int InstallBusy=1618, RestartRequired=3010;
        const int TaskNotFound=unchecked((int)0x80070002);
        static readonly TimeSpan Lifetime=TimeSpan.FromDays(7);
        static string Root {get{return Path.Combine(Path.GetDirectoryName(App.InstallRoot),"PhononSAPIUpdate");}}

        internal static void Request(string installer,UpdateInfo info) {
            string folder=Path.GetDirectoryName(installer), envelope=Path.Combine(folder,Manifest);
            // Keep the ordinary download cache layout unchanged after the elevated copy returns.
            using(var file=new FileStream(envelope,FileMode.CreateNew,FileAccess.Write))
            using(var writer=new StreamWriter(file))writer.Write(info.SignedManifest);
            bool queued=false;
            try {
                RunElevated(QueueOption+" \""+folder+"\"");
                queued=true;
            } finally {
                try {
                    File.Delete(envelope);
                    if(queued){File.Delete(installer);Directory.Delete(folder,false);}
                } catch(IOException error){App.Log(error.Message);} catch(UnauthorizedAccessException error){App.Log(error.Message);}
            }
        }
        internal static void RequestCancel() {RunElevated(CancelOption);}

        static void RunElevated(string arguments) {
            using(var process=Process.Start(new ProcessStartInfo(Path.Combine(App.InstallRoot,"PhononSAPI.exe"),arguments) {
                UseShellExecute=true,Verb="runas",WorkingDirectory=App.InstallRoot
            })) {
                if(process==null)throw new IOException("Windows could not start the update staging process.");
                process.WaitForExit();
                if(process.ExitCode!=0)throw new IOException("Windows did not confirm the requested change to the startup update.");
            }
        }

        internal static void Queue(string downloadFolder) {
            RequireInstalledAdministrator();
            string source=ValidateDownloadFolder(downloadFolder);
            string envelope=ReadManifest(Path.Combine(source,Manifest));
            UpdateInfo info=Updater.Verify(envelope);
            EnsureNewer(info);
            string download=Path.Combine(source,"Phonon-SAPI-"+info.version+".msi");
            RequireRegularPath(download);
            string installedRunner=Path.Combine(App.InstallRoot,"PhononSAPI.exe");
            RequireRegularPath(installedRunner);
            EnsureRoot();
            using(OpenStateLock()) {
                dynamic tasks=TaskFolder();
                if(TaskExists(tasks))throw new IOException("A Phonon SAPI update is already scheduled. Restart Windows to apply it.");
                ClearOldStage();
                try {
                    string package=Path.Combine(Root,Package);
                    using(var input=File.OpenRead(download)) {
                        VerifyPackage(input,info);
                        input.Position=0;
                        using(var output=new FileStream(package,FileMode.CreateNew,FileAccess.Write))input.CopyTo(output);
                    }
                    WindowsInstaller.Validate(package,info.version);
                    File.Copy(installedRunner,Path.Combine(Root,Runner),false);
                    File.WriteAllText(Path.Combine(Root,Manifest),envelope);
                    dynamic task=CreateTaskDefinition(Scheduler(),Root,DateTimeOffset.Now.Add(Lifetime));
                    tasks.RegisterTaskDefinition(TaskName,task,TaskCreate,"SYSTEM",null,ServiceAccountLogon,TaskAcl);
                    RequireOwnedTask(tasks.GetTask(TaskName));
                    WriteResult(PendingNotice);
                } catch {
                    // If registration might have succeeded, leave its verified files intact.
                    if(!TaskExists(tasks))ClearOldStage();
                    throw;
                }
            }
        }
        internal static void Cancel() {
            RequireInstalledAdministrator();
            VerifyRoot();
            using(OpenStateLock()) {
                dynamic tasks=TaskFolder();
                if(TaskExists(tasks)){RequireOwnedTask(tasks.GetTask(TaskName));tasks.DeleteTask(TaskName,0);}
                ClearOldStage();
                WriteResult("The scheduled Phonon SAPI update was cancelled.");
            }
        }
        static void RequireInstalledAdministrator() {
            using(var identity=WindowsIdentity.GetCurrent()) {
                if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ||
                   !String.Equals(App.Folder,App.InstallRoot,StringComparison.OrdinalIgnoreCase))
                    throw new SecurityException("Only the installed administrator-approved application can change a startup update.");
            }
        }

        internal static dynamic CreateTaskDefinition(dynamic scheduler,string stage,DateTimeOffset expires) {
            dynamic task=scheduler.NewTask(0);
            task.RegistrationInfo.Description="Installs one verified Phonon SAPI update at startup, then removes this task.";
            task.Principal.UserId="SYSTEM";
            task.Principal.LogonType=ServiceAccountLogon;
            task.Settings.AllowDemandStart=false;
            task.Settings.DisallowStartIfOnBatteries=false;
            task.Settings.StopIfGoingOnBatteries=false;
            task.Settings.ExecutionTimeLimit="PT30M";
            task.Settings.DeleteExpiredTaskAfter="PT1H";
            dynamic boot=task.Triggers.Create(BootTrigger);
            boot.EndBoundary=expires.ToString("yyyy-MM-dd'T'HH:mm:sszzz",CultureInfo.InvariantCulture);
            dynamic action=task.Actions.Create(ExecuteAction);
            action.Path=Path.Combine(stage,Runner);action.Arguments=ApplyOption;action.WorkingDirectory=stage;
            return task;
        }

        static void RequireOwnedTask(dynamic task) {
            dynamic definition=task.Definition;
            if((int)definition.Actions.Count!=1 || (int)definition.Triggers.Count!=1 ||
               (int)definition.Triggers.Item(1).Type!=BootTrigger || (int)definition.Principal.LogonType!=ServiceAccountLogon ||
               (bool)definition.Settings.AllowDemandStart)
                throw new SecurityException("The startup update task does not match the expected definition.");
            dynamic action=definition.Actions.Item(1);
            string principal=(string)definition.Principal.UserId;
            if((principal!=SystemSid && !String.Equals(principal,"SYSTEM",StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(principal,@"NT AUTHORITY\SYSTEM",StringComparison.OrdinalIgnoreCase)) ||
               !String.Equals((string)action.Path,Path.Combine(Root,Runner),StringComparison.OrdinalIgnoreCase) ||
               (string)action.Arguments!=ApplyOption || !String.Equals((string)action.WorkingDirectory,Root,StringComparison.OrdinalIgnoreCase))
                throw new SecurityException("The startup update task does not belong to Phonon SAPI.");
        }

        internal static int Apply() {
            using(var identity=WindowsIdentity.GetCurrent()) {
                if(!identity.IsSystem || !String.Equals(App.Folder,Root,StringComparison.OrdinalIgnoreCase))return 1;
            }
            VerifyRoot();
            using(OpenStateLock()) {
                int result=1;
                try {
                    string attempted=Path.Combine(Root,Attempt);
                    if(File.Exists(attempted))throw new IOException("A previous startup update was interrupted. No new attempt was made.");
                    using(new FileStream(attempted,FileMode.CreateNew,FileAccess.Write)){}
                    string envelope=Path.Combine(Root,Manifest), package=Path.Combine(Root,Package);
                    if(DateTime.UtcNow-File.GetLastWriteTimeUtc(envelope)>Lifetime)throw new IOException("The queued update expired. Check for updates again.");
                    var info=Updater.Verify(ReadManifest(envelope));
                    EnsureNewer(info);
                    RequireRegularPath(package);
                    using(var input=File.OpenRead(package)) {
                        VerifyPackage(input,info);
                        WindowsInstaller.Validate(package,info.version);
                        result=Install(package);
                    }
                    WriteResult(result==0?"Phonon SAPI "+info.version+" was installed at startup.":
                        result==RestartRequired?"Windows Installer needs another restart to finish the Phonon SAPI update.":
                        "The startup update did not finish (Windows Installer code "+result+"). Check for updates again.");
                } catch(Exception error) {
                    WriteResult("The Phonon SAPI startup update did not run: "+error.Message);
                } finally {
                    // Keep only the small runner and the result; the task cannot be run on demand.
                    DeleteOwned(Package);DeleteOwned(Manifest);
                    dynamic tasks=TaskFolder();
                    if(TaskExists(tasks)){RequireOwnedTask(tasks.GetTask(TaskName));tasks.DeleteTask(TaskName,0);}
                }
                return result;
            }
        }

        internal static string Notice() {
            try {
                VerifyRoot();
                string path=Path.Combine(Root,Result);
                RequireRegularPath(path);
                if(new FileInfo(path).Length>4096)return "";
                string text=File.ReadAllText(path);
                if(File.Exists(Path.Combine(Root,Package)) && DateTime.UtcNow-File.GetLastWriteTimeUtc(path)>Lifetime)
                    return "The scheduled Phonon SAPI update expired. Check for updates again.";
                return text;
            } catch(IOException){return "";} catch(UnauthorizedAccessException){return "";} catch(SecurityException){return "";}
        }

        internal static string ValidateDownloadFolder(string path) {
            string full=Path.GetFullPath(path);
            if(!String.Equals(path,full,StringComparison.OrdinalIgnoreCase) || full.Length<4 || full[1]!=':' || full[2]!='\\' ||
                !Regex.IsMatch(Path.GetFileName(full),@"\A[0-9a-f]{32}\z") ||
                !full.EndsWith(@"\PhononSAPI\updates\"+Path.GetFileName(full),StringComparison.OrdinalIgnoreCase))
                throw new SecurityException("The update download is not in a supported staging folder.");
            RequireRegularPath(full);
            return full;
        }
        internal static void VerifyPackage(Stream stream,UpdateInfo info) {
            if(stream.Length!=info.size || Installer.Hash(stream)!=info.sha256)
                throw new SecurityException("The queued update does not match the signed package.");
        }
        static void EnsureNewer(UpdateInfo info) {
            if(new Version(info.version)<=WindowsInstaller.InstalledVersion())throw new IOException("The queued package is not newer than the installed version.");
        }
        static string ReadManifest(string path) {
            RequireRegularPath(path);
            using(var file=File.OpenRead(path)) {
                if(file.Length>128*1024)throw new IOException("Update information is too large.");
                using(var reader=new StreamReader(file))return reader.ReadToEnd();
            }
        }
        static void RequireRegularPath(string path) {
            for(string current=path;current!=null;current=Path.GetDirectoryName(current))
                if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new SecurityException("The update path is redirected.");
        }
        static void EnsureRoot() {
            RequireRegularPath(Path.GetDirectoryName(Root));
            if(!Directory.Exists(Root)) {
                var acl=new DirectorySecurity();acl.SetSecurityDescriptorSddlForm(StageAcl);
                Directory.CreateDirectory(Root,acl);
            }
            VerifyRoot();
        }
        static void VerifyRoot() {
            RequireRegularPath(Root);
            var acl=Directory.GetAccessControl(Root);
            string owner=acl.GetOwner(typeof(SecurityIdentifier)).Value;
            if(!acl.AreAccessRulesProtected || (owner!=AdminSid && owner!=SystemSid))throw new SecurityException("The machine update folder is not protected.");
            var rules=acl.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            if(rules.Any(rule=>rule.AccessControlType==AccessControlType.Allow && rule.IdentityReference.Value!=AdminSid &&
                rule.IdentityReference.Value!=SystemSid && (rule.IdentityReference.Value!=UsersSid ||
                (rule.FileSystemRights & ~(FileSystemRights.ReadAndExecute|FileSystemRights.Synchronize))!=0)))
                throw new SecurityException("The machine update folder is writable by another account.");
        }
        static FileStream OpenStateLock() {
            string path=Path.Combine(Root,StateLock);
            if(File.Exists(path))RequireRegularPath(path);
            return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        }
        static void ClearOldStage() {foreach(string name in new[]{Runner,Manifest,Package,Attempt,Result})DeleteOwned(name);}
        static void DeleteOwned(string name) {
            string path=Path.Combine(Root,name);
            if(File.Exists(path)){RequireRegularPath(path);File.Delete(path);}
        }
        static void WriteResult(string text) {
            DeleteOwned(Result);
            using(var stream=new FileStream(Path.Combine(Root,Result),FileMode.CreateNew,FileAccess.Write))
            using(var writer=new StreamWriter(stream))writer.Write(text);
        }
        static dynamic Scheduler() {dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",true));service.Connect();return service;}
        static dynamic TaskFolder() {return Scheduler().GetFolder("\\");}
        static bool TaskExists(dynamic folder) {
            try{return folder.GetTask(TaskName)!=null;}
            catch(COMException error){if(error.ErrorCode==TaskNotFound)return false;throw;}
            catch(FileNotFoundException error){if(error.HResult==TaskNotFound)return false;throw;}
        }
        static int Install(string package) {
            string executable=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"msiexec.exe");
            for(int attempt=0;attempt<12;attempt++) {
                using(var process=Process.Start(new ProcessStartInfo(executable,"/i \""+package+"\" /qn /norestart") {
                    UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Root
                })) {
                    if(process==null)throw new IOException("Windows Installer could not be started.");
                    process.WaitForExit();
                    if(process.ExitCode!=InstallBusy || attempt==11)return process.ExitCode;
                }
                Thread.Sleep(5000);
            }
            return InstallBusy;
        }
    }
}
