// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using FILETIME=System.Runtime.InteropServices.ComTypes.FILETIME;

namespace PhononSapi {
    internal static class WindowsInstaller {
        const uint Success=0, MoreData=234, NoMoreItems=259, MaximumApplications=1024;
        static readonly string[] PhononFiles={"PhononSAPI.exe","PhononHost.exe","PhononSAPI32.dll","PhononSAPI64.dll",
            @"runtime\ptts_ffi.dll",@"runtime\libunwind.dll"};
        static readonly HashSet<string> ScreenReaders=new HashSet<string>(new[]{"nvda","jfw","jaws","narrator","zdsr","dolphin","supernova","wineyes","orca"},StringComparer.OrdinalIgnoreCase);
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
        internal struct ResourceUser {
            public int ProcessId;
            public FILETIME Started;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string Service;
            public uint Type,Status,Session;
            [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
            internal long StartTicks {get{return ((long)Started.dwHighDateTime<<32)|(uint)Started.dwLowDateTime;}}
            internal string DisplayName {get{string name=String.IsNullOrWhiteSpace(Name)?Service:Name;return String.IsNullOrWhiteSpace(name)?"An app Windows could not identify":name;}}
        }
        [StructLayout(LayoutKind.Sequential)]
        struct UniqueProcess {public int Id;public FILETIME Started;}
        [DllImport("rstrtmgr.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] static extern uint RmStartSession(out uint session,uint flags,StringBuilder key);
        [DllImport("rstrtmgr.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] static extern uint RmRegisterResources(uint session,uint files,string[] paths,uint apps,IntPtr users,uint services,IntPtr names);
        [DllImport("rstrtmgr.dll")] static extern uint RmGetList(uint session,out uint needed,ref uint count,[Out] ResourceUser[] users,out uint reasons);
        [DllImport("rstrtmgr.dll")] static extern uint RmEndSession(uint session);
        [DllImport("rstrtmgr.dll",CharSet=CharSet.Unicode,ExactSpelling=true,EntryPoint="RmRegisterResources")]
        static extern uint RmRegisterProcesses(uint session,uint files,IntPtr paths,uint apps,UniqueProcess[] users,uint services,IntPtr names);
        [DllImport("rstrtmgr.dll")] static extern uint RmShutdown(uint session,uint flags,IntPtr callback);
        [DllImport("rstrtmgr.dll")] static extern uint RmRestart(uint session,uint flags,IntPtr callback);
        [DllImport("kernel32.dll",ExactSpelling=true)] static extern int GetApplicationRestartSettings(IntPtr process,IntPtr args,ref uint size,out uint flags);
        [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
        [DllImport("advapi32.dll")] static extern bool GetTokenInformation(IntPtr token,int informationClass,out int value,int length,out int returned);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiOpenDatabase(string path,IntPtr mode,out uint database);
        [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiDatabaseOpenView(uint database,string query,out uint view);
        [DllImport("msi.dll")] static extern uint MsiViewExecute(uint view,uint record);
        [DllImport("msi.dll")] static extern uint MsiViewFetch(uint view,out uint record);
        [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiRecordGetString(uint record,uint field,StringBuilder value,ref uint length);
        [DllImport("msi.dll")] static extern uint MsiCloseHandle(uint handle);
        [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiEnumRelatedProducts(string upgradeCode,uint reserved,uint index,StringBuilder productCode);
        [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiGetProductInfo(string productCode,string property,StringBuilder value,ref uint length);
        internal static Dictionary<string,string> Metadata(string path) {
            uint database;
            if(MsiOpenDatabase(path,IntPtr.Zero,out database)!=Success)throw new InvalidDataException("The download is not a valid Windows Installer package.");
            try {
                var result=new Dictionary<string,string>();
                foreach(string name in new[]{"ProductName","ProductVersion","UpgradeCode"}) {
                    uint view=0,record=0;
                    try {
                        if(MsiDatabaseOpenView(database,"SELECT `Value` FROM `Property` WHERE `Property`='"+name+"'",out view)!=Success ||
                           MsiViewExecute(view,0)!=Success || MsiViewFetch(view,out record)!=Success)throw new InvalidDataException("The installer is missing product information.");
                        var value=new StringBuilder(1024);uint length=1024;
                        if(MsiRecordGetString(record,1,value,ref length)!=Success)throw new InvalidDataException("The installer product information is invalid.");
                        result[name]=value.ToString();
                    }finally{if(record!=0)MsiCloseHandle(record);if(view!=0)MsiCloseHandle(view);}
                }
                return result;
            }finally{MsiCloseHandle(database);}
        }
        internal static void Validate(string path,string version) {
            var metadata=Metadata(path);
            if(metadata["ProductName"]!="Phonon SAPI" || metadata["ProductVersion"]!=version ||
               !String.Equals(metadata["UpgradeCode"],App.UpgradeCode,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installer does not match the signed Phonon SAPI update.");
        }
        internal static string InstalledProduct() {
            for(uint index=0;index<16;index++) {
                var code=new StringBuilder(39);uint status=MsiEnumRelatedProducts(App.UpgradeCode,0,index,code);
                if(status==NoMoreItems)break;
                if(status!=Success)throw new IOException("Windows could not find the installed Phonon package.");
                var folder=new StringBuilder(32768);uint length=32768;
                if(MsiGetProductInfo(code.ToString(),"InstallLocation",folder,ref length)==Success &&
                   String.Equals(folder.ToString().TrimEnd('\\'),App.InstallRoot,StringComparison.OrdinalIgnoreCase))return code.ToString();
            }
            throw new InvalidOperationException("Install Phonon SAPI using its Windows Installer package first.");
        }
        internal static Version InstalledVersion() {
            var value=new StringBuilder(32);uint length=32;
            if(MsiGetProductInfo(InstalledProduct(),"VersionString",value,ref length)!=Success)
                throw new IOException("Windows could not read the installed Phonon SAPI version.");
            return new Version(value.ToString());
        }
        internal static string[] BlockerNames(ResourceUser[] users) {
            return users.Select(user=>user.DisplayName).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(name=>name,StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        internal static string[] FindBlockers(string folder,int ignoredProcessId) {return BlockerNames(FindResourceUsers(folder,ignoredProcessId));}
        internal static ResourceUser[] FindResourceUsers(string folder,int ignoredProcessId) {
            var paths=new List<string>();
            foreach(string name in PhononFiles) {
                string path=Path.Combine(folder,name);
                if(File.Exists(path))paths.Add(path);
            }
            if(paths.Count==0)return new ResourceUser[0];
            uint session;
            CheckRestartManager(RmStartSession(out session,0,new StringBuilder(33)));
            try {
                CheckRestartManager(RmRegisterResources(session,(uint)paths.Count,paths.ToArray(),0,IntPtr.Zero,0,IntPtr.Zero));
                ResourceUser[] users=null;
                for(int attempt=0;attempt<4;attempt++) {
                    uint count=users==null?0:(uint)users.Length,needed,reasons;
                    uint result=RmGetList(session,out needed,ref count,users,out reasons);
                    if(result==MoreData && needed>count && needed<=MaximumApplications) {
                        users=new ResourceUser[needed];
                        continue;
                    }
                    CheckRestartManager(result);
                    return count==0?new ResourceUser[0]:users.Take((int)count).Where(user=>user.ProcessId!=ignoredProcessId).ToArray();
                }
                throw new IOException("The applications using Phonon changed during the check. Please retry.");
            }finally{RmEndSession(session);}
        }
        internal static bool CanRestart(ResourceUser[] users) {
            const uint QueryToken=8, NoPatch=4;
            const int MainWindow=1, OtherWindow=2, TokenElevation=20, TokenUiAccess=26;
            if(users.Length==0)return false;
            using(var current=Process.GetCurrentProcess())using(var identity=WindowsIdentity.GetCurrent()) {
                foreach(var user in users) {
                    if(!user.Restartable || (user.Type!=MainWindow && user.Type!=OtherWindow) || user.Session!=current.SessionId)return false;
                    try {
                        using(var process=Process.GetProcessById(user.ProcessId)) {
                            if(process.StartTime.ToFileTimeUtc()!=user.StartTicks || DateTime.UtcNow-process.StartTime.ToUniversalTime()<TimeSpan.FromMinutes(1))return false;
                            if(ScreenReaders.Contains(Path.GetFileNameWithoutExtension(process.MainModule.FileName)))return false;
                            IntPtr token;
                            if(!OpenProcessToken(process.Handle,QueryToken,out token))return false;
                            try {
                                int value,returned;
                                if(!GetTokenInformation(token,TokenElevation,out value,4,out returned) || value!=0 ||
                                   !GetTokenInformation(token,TokenUiAccess,out value,4,out returned) || value!=0)return false;
                                using(var owner=new WindowsIdentity(token))if(!owner.User.Equals(identity.User))return false;
                            }finally{CloseHandle(token);}
                            uint size=0,flags;
                            if(GetApplicationRestartSettings(process.Handle,IntPtr.Zero,ref size,out flags)!=0 || (flags&NoPatch)!=0)return false;
                        }
                    }catch(ArgumentException){return false;}catch(InvalidOperationException){return false;}catch(Win32Exception){return false;}
                }
            }
            return true;
        }
        internal static void CloseAndRestore(ResourceUser[] users,Action install) {
            const uint OnlyRegistered=0x10;
            if(!CanRestart(users))throw new IOException("The apps cannot all be safely reopened. No apps were closed.");
            uint session;
            CheckRestartManager(RmStartSession(out session,0,new StringBuilder(33)));
            try {
                // Register only the exact instances approved by the user, never a resource search at shutdown time.
                var exact=users.Select(user=>new UniqueProcess {Id=user.ProcessId,Started=user.Started}).ToArray();
                CheckRestartManager(RmRegisterProcesses(session,0,IntPtr.Zero,(uint)exact.Length,exact,0,IntPtr.Zero));
                uint closed=Success;
                Exception updateFailure=null;
                try {
                    closed=RmShutdown(session,OnlyRegistered,IntPtr.Zero);
                    if(closed!=Success)throw new IOException("Windows could not close every approved app. The update was not installed.",new Win32Exception((int)closed));
                    install();
                } catch(Exception error) {
                    updateFailure=error;
                    throw;
                } finally {
                    const uint RestartNotNeeded=776;
                    uint restored=RmRestart(session,0,IntPtr.Zero);
                    if(restored!=Success && !(restored==RestartNotNeeded && closed!=Success)) {
                        string message="Windows could not reopen every app. Open any missing app from the Start menu.";
                        if(updateFailure!=null)message=updateFailure.Message+"\n\n"+message;
                        throw new IOException(message,new Win32Exception((int)restored));
                    }
                }
            }finally{RmEndSession(session);}
        }
        static void CheckRestartManager(uint result) {
            if(result!=Success)throw new IOException("Windows could not check which applications are using Phonon. Please retry.",new Win32Exception((int)result));
        }
        static Process Start(string arguments) {
            string executable=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"msiexec.exe");
            var process=Process.Start(new ProcessStartInfo(executable,arguments+" /norestart") {UseShellExecute=true,Verb="runas"});
            if(process==null)throw new IOException("Windows Installer could not be started.");
            return process;
        }
        static Process StartInstall(string path) {return Start("/i \""+Path.GetFullPath(path)+"\" /qb+");}
        internal static void Install(string path) {using(StartInstall(path)){} }
        internal static int InstallAndWait(string path) {using(var process=StartInstall(path)){process.WaitForExit();return process.ExitCode;} }
        internal static void Maintenance(bool remove) {using(Start((remove?"/x ":"/fa ")+InstalledProduct())){} }
    }
}
