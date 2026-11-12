// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using System.Reflection;

namespace PhononSapi {
    static class UpdateCacheTests {
        static void Assert(bool value,string message) {if(!value)throw new Exception(message);}
        public static int Main(string[] args) {
            var assembly=Assembly.LoadFile(Path.GetFullPath(args[0]));
            var method=assembly.GetType("PhononSapi.Updater").GetMethod("CleanOldDownloads",BindingFlags.NonPublic|BindingFlags.Static);
            Assert(method!=null,"Missing bounded update-cache cleanup");
            string root=Path.GetFullPath(args[1]);
            Assert(!Directory.Exists(root),"Use an empty isolated test directory");
            Directory.CreateDirectory(root);
            DateTime now=DateTime.UtcNow;
            Func<string,string,string> create=(id,name)=>{string dir=Path.Combine(root,id);Directory.CreateDirectory(dir);string file=Path.Combine(dir,name);File.WriteAllText(file,"test");File.SetLastWriteTimeUtc(file,now.AddDays(-2));return file;};
            try {
                string stale=create(new string('a',32),"Phonon-SAPI-1.0.0.msi");
                string recent=create(new string('b',32),"Phonon-SAPI-1.0.0.msi");File.SetLastWriteTimeUtc(recent,now);
                string unknown=create(new string('c',32),"personal.txt");
                string extra=create(new string('d',32),"Phonon-SAPI-1.0.0.msi");File.WriteAllText(Path.Combine(Path.GetDirectoryName(extra),"keep.txt"),"keep");
                string foreign=create("not-an-update-id","Phonon-SAPI-1.0.0.msi");
                string locked=create(new string('e',32),"Phonon-SAPI-1.0.0.msi");
                string staleHelper=create(new string('f',32),"PhononCooperativeUpdate.exe");
                string lockedHelper=create(new string('0',32),"PhononCooperativeUpdate.exe");
                string lockedHelperManifest=create(new string('0',32),"phononSapi-update.json");
                using(var held=new FileStream(locked,FileMode.Open,FileAccess.Read,FileShare.None))
                using(var active=new FileStream(lockedHelper,FileMode.Open,FileAccess.Read,FileShare.Read))
                    method.Invoke(null,new object[]{root,now});
                Assert(!Directory.Exists(Path.GetDirectoryName(stale)),"Stale download retained");
                Assert(!Directory.Exists(Path.GetDirectoryName(staleHelper)),"Stale cooperative helper retained");
                foreach(string file in new[]{recent,unknown,extra,foreign,locked,lockedHelper,lockedHelperManifest})Assert(File.Exists(file),"Unrelated/recent/locked file removed");
                method.Invoke(null,new object[]{root,now});
                Assert(!Directory.Exists(Path.GetDirectoryName(locked)),"Unlocked stale download retained");
                Assert(!Directory.Exists(Path.GetDirectoryName(lockedHelper)),"Unlocked cooperative helper retained");
                Console.WriteLine("Update cache: stale removed; recent, unknown, extra-content and locked paths preserved");
                return 0;
            } finally {Directory.Delete(root,true);}
        }
    }
}
