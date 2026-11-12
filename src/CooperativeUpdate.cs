// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace PhononSapi {
    internal static class CooperativeUpdate {
        internal const string Option="--install-with-app-restart", Runner="PhononCooperativeUpdate.exe";
        const string Manifest="phononSapi-update.json";
        const int RestartRequired=3010;

        internal static string Approval(WindowsInstaller.ResourceUser[] users) {
            return String.Join(",",users.Select(user=>user.ProcessId.ToString(CultureInfo.InvariantCulture)+":"+
                user.StartTicks.ToString(CultureInfo.InvariantCulture)).OrderBy(value=>value,StringComparer.Ordinal));
        }

        internal static void Start(string package,UpdateInfo update,WindowsInstaller.ResourceUser[] approved) {
            if(!String.Equals(App.Folder,App.InstallRoot,StringComparison.OrdinalIgnoreCase) || !WindowsInstaller.CanRestart(approved))
                throw new IOException("The approved apps changed. No apps were closed. Check for updates again.");
            string folder=Path.GetDirectoryName(package);
            string runner=Path.Combine(folder,Runner), manifest=Path.Combine(folder,Manifest);
            VerifyFolder(folder);
            bool copied=false, written=false;
            try {
                File.Copy(Path.Combine(App.InstallRoot,"PhononSAPI.exe"),runner,false);copied=true;
                using(var file=new FileStream(manifest,FileMode.CreateNew,FileAccess.Write)) {
                    written=true;
                    using(var writer=new StreamWriter(file))writer.Write(update.SignedManifest);
                }
                using(var current=Process.GetCurrentProcess()) {
                    string args=String.Format(CultureInfo.InvariantCulture,"{0} {1} {2} {3}",Option,current.Id,current.StartTime.ToFileTimeUtc(),Approval(approved));
                    using(var helper=Process.Start(new ProcessStartInfo(runner,args){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=folder})) {
                        if(helper==null)throw new IOException("Windows could not start the update helper.");
                    }
                }
            } catch {
                if(written)File.Delete(manifest);
                if(copied)File.Delete(runner);
                throw;
            }
        }

        internal static int Run(string[] args) {
            string manifest=null, package=null;
            try {
                VerifyFolder(App.Folder);
                if(!String.Equals(Path.GetFileName(typeof(App).Assembly.Location),Runner,StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The update helper is not in its staging folder.");
                int parent;long birth;
                if(!Int32.TryParse(args[1],NumberStyles.None,CultureInfo.InvariantCulture,out parent) || parent<=0 ||
                   !Int64.TryParse(args[2],NumberStyles.None,CultureInfo.InvariantCulture,out birth) || birth<=0 ||
                   args[3].Length>1024 || !Regex.IsMatch(args[3],@"\A[0-9]+:[0-9]+(?:,[0-9]+:[0-9]+)*\z"))
                    throw new InvalidDataException("The update approval is invalid.");
                WaitForSettingsExit(parent,birth);
                manifest=Path.Combine(App.Folder,Manifest);
                var update=Updater.Verify(ReadManifest(manifest));
                if(new Version(update.version)<=WindowsInstaller.InstalledVersion())throw new IOException("This update is no longer newer than the installed version.");
                package=Path.Combine(App.Folder,"Phonon-SAPI-"+update.version+".msi");
                if((File.GetAttributes(package)&FileAttributes.ReparsePoint)!=0)throw new IOException("The update package is redirected.");
                int exitCode=0;
                using(var input=File.OpenRead(package)) {
                    RestartUpdate.VerifyPackage(input,update);
                    WindowsInstaller.Validate(package,update.version);
                    var users=WindowsInstaller.FindResourceUsers(App.InstallRoot,0);
                    if(Approval(users)!=args[3] || !WindowsInstaller.CanRestart(users))
                        throw new IOException("The apps using Phonon changed. No apps were closed. Open Phonon SAPI to try again.");
                    WindowsInstaller.CloseAndRestore(users,()=>{
                        var late=WindowsInstaller.FindBlockers(App.InstallRoot,0);
                        if(late.Length!=0)throw new IOException("The update is still blocked by: "+String.Join(", ",late)+".");
                        exitCode=WindowsInstaller.InstallAndWait(package);
                        if(exitCode!=0 && exitCode!=RestartRequired)throw new IOException("Windows Installer did not complete the update (code "+exitCode+").");
                    });
                }
                MessageBox.Show(exitCode==RestartRequired?"Restart Windows when ready to finish the Phonon SAPI update.":"Phonon SAPI was updated.",
                    "Phonon SAPI updates",MessageBoxButtons.OK,MessageBoxIcon.Information);
                return 0;
            } catch(Exception error) {
                App.Log(error.ToString());
                MessageBox.Show(error.Message,"Phonon SAPI updates",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;
            } finally {
                foreach(string path in new[]{manifest,package})if(path!=null) {
                    try{if(File.Exists(path) && (File.GetAttributes(path)&FileAttributes.ReparsePoint)==0)File.Delete(path);}
                    catch(IOException error){App.Log(error.Message);}catch(UnauthorizedAccessException error){App.Log(error.Message);}
                }
            }
        }

        static string ReadManifest(string path) {
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("The signed update manifest is redirected.");
            using(var file=File.OpenRead(path)) {
                if(file.Length>128*1024)throw new InvalidDataException("The update information is too large.");
                using(var reader=new StreamReader(file))return reader.ReadToEnd();
            }
        }
        static void VerifyFolder(string folder) {
            RestartUpdate.ValidateDownloadFolder(folder);
            if(!String.Equals(Path.GetDirectoryName(folder),Path.Combine(App.Data,"updates"),StringComparison.OrdinalIgnoreCase))
                throw new IOException("The update helper must use this user's update cache.");
        }
        static void WaitForSettingsExit(int pid,long birth) {
            Process process;
            try{process=Process.GetProcessById(pid);}catch(ArgumentException){return;}
            using(process) {
                if(process.StartTime.ToFileTimeUtc()!=birth)return;
                if(!process.WaitForExit(15000))throw new IOException("The Phonon SAPI settings window is still running. No apps were closed.");
            }
        }
    }
}
