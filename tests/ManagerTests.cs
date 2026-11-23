// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace PhononSapi {
    public static class ManagerTests {
        sealed class FileHandler : HttpMessageHandler {
            readonly string file;
            public FileHandler(string path){file=path;}
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation) {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(File.OpenRead(file))});
            }
        }
        static int count;
        static void Assert(bool value){if(!value)throw new Exception("Assertion failed: "+count);count++;}
        static void Reject(Action test){bool rejected=false;try{test();}catch{rejected=true;}Assert(rejected);}
        static string Url(string version){return "https://github.com/OnjLouis/phononSAPI/releases/download/v"+version+"/Phonon-SAPI-"+version+".msi";}
        public static int Main(string[] args) {
            Assert(App.ParsePreferences("{}").PauseLength==0);
            Assert(App.ParsePreferences("{}").VoiceVariation==0);
            Assert(App.ParsePreferences("{}").GraphicsAdapter=="");
            Assert(App.ParsePreferences("{\"VoiceVariation\":-1,\"GraphicsAdapter\":\"invalid\"}").VoiceVariation==0);
            Assert(App.ParsePreferences("{\"VoiceVariation\":3,\"GraphicsAdapter\":\"invalid\"}").GraphicsAdapter=="");
            const string adapter="0123456789abcdef0123456789abcdef";
            var graphics=App.ParsePreferences(App.Json.Serialize(new Preferences{GraphicsAdapter=adapter,VoiceVariation=2}));
            Assert(graphics.GraphicsAdapter==adapter && graphics.VoiceVariation==2);
            var adapters=GraphicsChoice.Parse("invalid\n"+adapter+"\tDiscrete GPU\n"+adapter+"\tDuplicate\n");
            Assert(adapters.Length==1 && adapters[0].Id==adapter && adapters[0].Name=="Discrete GPU");
            Assert(!App.ParsePreferences("{}").PlayOnVoiceChange);
            Assert(App.ParsePreferences(App.Json.Serialize(new Preferences{PlayOnVoiceChange=true})).PlayOnVoiceChange);
            Assert(App.ParsePreferences("{\"PauseLength\":-1}").PauseLength==0);
            Assert(App.ParsePreferences("{\"PauseLength\":3}").PauseLength==0);
            foreach(int pause in new[]{0,1,2}) {
                Assert(App.ParsePreferences(App.Json.Serialize(new Preferences{PauseLength=pause})).PauseLength==pause);
            }
            string folder=Path.GetFullPath(args[0]);
            Assert(Installer.SafePath(folder,"model/config.json").StartsWith(folder));
            foreach(string path in new[]{"../escape","/absolute","C:/file","model\\config.json","a/../file","a//file","a/./file"})Reject(()=>Installer.SafePath(folder,path));
            Installer.Validate(folder);count++;
            Assert(App.Voices(folder).Count==102);
            string profile=Path.Combine(Path.GetDirectoryName(folder),"profile-"+Guid.NewGuid().ToString("N"));
            try {
                string stock=Path.Combine(folder,"model","voices","Freya.safetensors");
                var store=new ImportedVoices(profile,folder);
                var id=store.Add(stock,"Fixture","en-GB");
                byte[] tensor=store.ReadVoice(store.List().Single());
                store.ChangeLanguage(id,"fr-FR");
                Reject(()=>store.ChangeLanguage(id,"ja-JP"));
                var updated=store.List().Single();
                Assert(updated.id==id && updated.name=="Fixture" && updated.locale=="fr-FR");
                Assert(tensor.SequenceEqual(store.ReadVoice(updated)));
                store.Remove(id);
                Assert(!store.List().Any());
            } finally {if(Directory.Exists(profile))Directory.Delete(profile,true);}
            using(var rsa=new RSACryptoServiceProvider(2048)) {
                rsa.PersistKeyInCsp=false;
                var key=rsa.ExportParameters(false);
                Func<UpdateInfo,string> sign=value=>{
                    byte[] bytes=Encoding.UTF8.GetBytes(App.Json.Serialize(value));
                    return App.Json.Serialize(new Envelope{payload=Convert.ToBase64String(bytes),signature=Convert.ToBase64String(rsa.SignData(bytes,"SHA256"))});
                };
                var candidate=new UpdateInfo{product="phononSapi",version=App.Version,url=Url(App.Version),sha256=new string('0',64),size=1234,changes="Test"};
                string good=sign(candidate);
                Assert(Updater.VerifyWithKey(good,key).version==App.Version);
                Reject(()=>Updater.Verify(good));
                var envelope=App.Json.Deserialize<Envelope>(good);
                byte[] altered=Convert.FromBase64String(envelope.payload);altered[altered.Length/2]^=1;
                Reject(()=>Updater.VerifyWithKey(App.Json.Serialize(new Envelope{payload=Convert.ToBase64String(altered),signature=envelope.signature}),key));
                Reject(()=>Updater.Verify("{}"));Reject(()=>Updater.Verify(new string('x',128*1024+1)));
                candidate.product="phononLocal";Reject(()=>Updater.VerifyWithKey(sign(candidate),key));candidate.product="phononSapi";
                candidate.url="https://example.com/foreign.msi";Reject(()=>Updater.VerifyWithKey(sign(candidate),key));
                candidate.url=Url(App.Version).Replace(".msi",".zip");Reject(()=>Updater.VerifyWithKey(sign(candidate),key));candidate.url=Url(App.Version);
                candidate.size=0;Reject(()=>Updater.VerifyWithKey(sign(candidate),key));candidate.size=1234;
                candidate.sha256="invalid";Reject(()=>Updater.VerifyWithKey(sign(candidate),key));
            }
            if(args.Length>1) {
                WindowsInstaller.Validate(args[1],App.Version);count++;
                Reject(()=>WindowsInstaller.Validate(args[1],"99.0.0"));
                var metadata=WindowsInstaller.Metadata(args[1]);
                Assert(metadata["ProductName"]=="Phonon SAPI");
                Assert(String.Equals(metadata["UpgradeCode"],App.UpgradeCode,StringComparison.OrdinalIgnoreCase));
                string settings=Path.Combine(Path.GetDirectoryName(folder),"msi-updater-test-settings");
                typeof(App).GetField("Data").SetValue(null,settings);
                var download=new UpdateInfo{product="phononSapi",version=App.Version,url=Url(App.Version),size=new FileInfo(args[1]).Length};
                using(var file=File.OpenRead(args[1]))download.sha256=Installer.Hash(file);
                using(var client=new HttpClient(new FileHandler(args[1]))) {
                    string staged=Updater.DownloadWithClient(download,CancellationToken.None,client).GetAwaiter().GetResult();
                    WindowsInstaller.Validate(staged,App.Version);count++;
                    Assert(staged.EndsWith(".msi") && Path.GetDirectoryName(staged).StartsWith(Path.Combine(settings,"updates")));
                    Directory.Delete(Path.GetDirectoryName(staged),true);
                    string validHash=download.sha256;
                    download.sha256=new string('0',64);
                    Reject(()=>Updater.DownloadWithClient(download,CancellationToken.None,client).GetAwaiter().GetResult());
                    download.sha256=validHash;download.size--;
                    Reject(()=>Updater.DownloadWithClient(download,CancellationToken.None,client).GetAwaiter().GetResult());download.size++;
                    download.version="99.0.0";download.url=Url(download.version);
                    Reject(()=>Updater.DownloadWithClient(download,CancellationToken.None,client).GetAwaiter().GetResult());
                    Assert(Directory.GetDirectories(Path.Combine(settings,"updates")).Length==0);
                }
                if(args.Length>2) {
                    var signed=Updater.Verify(File.ReadAllText(args[2]));Assert(signed.version==App.Version);
                    using(var file=File.OpenRead(args[1]))Assert(Installer.Hash(file)==signed.sha256);
                    Assert(new FileInfo(args[1]).Length==signed.size);
                }
                if(args.Length>3)Reject(()=>WindowsInstaller.Validate(args[3],App.Version));
            }
            Console.WriteLine("{\"manager_checks\":"+count+",\"signature_and_path_checks\":true,\"msi_validation\":true}");return 0;
        }
    }
}
