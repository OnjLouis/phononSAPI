// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PhononSapi {
    internal static class WindowsInstaller {
        const uint Success=0, NoMoreItems=259;
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
        internal static void Start(string arguments) {
            string executable=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"msiexec.exe");
            using(var process=Process.Start(new ProcessStartInfo(executable,arguments+" /norestart") {UseShellExecute=true,Verb="runas"})) {
                if(process==null)throw new IOException("Windows Installer could not be started.");
            }
        }
        internal static void Install(string path) {Start("/i \""+Path.GetFullPath(path)+"\"");}
        internal static void Maintenance(bool remove) {Start((remove?"/x ":"/fa ")+InstalledProduct());}
    }
}
