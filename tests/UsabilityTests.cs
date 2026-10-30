// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
namespace PhononSapi {
    public static class UsabilityTests {
        static int failures;
        static void Test(string name,Action test) {
            try {test();Console.WriteLine("PASS "+name);}
            catch(Exception error){failures++;Console.WriteLine("FAIL "+name+": "+(error.InnerException??error).Message);}
        }
        static void Assert(bool condition){if(!condition)throw new Exception("Unexpected result");}
        static object Call(Type type,string name,params object[] args){
            var method=type.GetMethod(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static);
            if(method==null)throw new Exception("Missing "+name);
            return method.Invoke(null,args);
        }
        static Preferences Parse(string json){return (Preferences)Call(typeof(App),"ParsePreferences",json);}
        public static int Main(string[] args) {
            if(args.Length==1 && args[0]=="--https-probe") {
                using(var client=Updater.Client())using(var response=client.GetAsync("https://api.github.com/repos/gradium-ai/xn-ptts/releases/latest").GetAwaiter().GetResult()) {
                    response.EnsureSuccessStatusCode();Console.WriteLine("Secure HTTPS succeeded using system TLS defaults.");return 0;
                }
            }
            string folder=args[0];
            Test("neutral controls use 50",()=>Assert(new Preferences().Rate==50 && new Preferences().Pitch==50));
            Test("legacy settings retain effective controls",()=>{
                var old=Parse("{\"Rate\":5,\"Pitch\":-5,\"Volume\":80,\"Voice\":\"saved\"}");
                Assert(old.Rate==75 && old.Pitch==25 && old.Volume==80 && old.Voice=="saved");
                var again=Parse(App.Json.Serialize(old));Assert(again.Rate==75 && again.Pitch==25);
            });
            Test("new control endpoints stay intact",()=>{
                var current=Parse("{\"ControlScale\":2,\"Rate\":0,\"Pitch\":100}");Assert(current.Rate==0 && current.Pitch==100);
                Assert((int)Call(typeof(App),"ToSapiControl",0)==-10);
                Assert((int)Call(typeof(App),"ToSapiControl",50)==0);
                Assert((int)Call(typeof(App),"ToSapiControl",75)==5);
                Assert((int)Call(typeof(App),"ToSapiControl",100)==10);
            });
            Test("diagnostics are bounded and independent of Explorer",()=>{
                string path=Path.Combine(Path.GetDirectoryName(folder),"diagnostics-fixture");Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path,"host.log"),new string('x',300000)+"\nrecent entry\n");
                string text=(string)Call(typeof(App),"ReadDiagnostics",path);
                Assert(text.Contains("recent entry") && text.Length<140000);
            });
            Test("backend status distinguishes actual results",()=>{
                var type=typeof(App).Assembly.GetType("PhononSapi.BackendStatus");if(type==null)throw new Exception("Missing backend status interface");
                Assert(((string)Call(type,"Describe",new int[]{2},true,false)).Contains("GPU (Vulkan)"));
                Assert(((string)Call(type,"Describe",new int[]{3},false,false)).Contains("GPU disabled"));
                Assert(((string)Call(type,"Describe",new int[]{4},true,false)).Contains("CPU fallback"));
                Assert(((string)Call(type,"Describe",new int[]{},true,false)).Contains("Not initialized"));
            });
            Test("Sonic licence has one package location",()=>Assert(!File.Exists(Path.Combine(folder,"SONIC-LICENSE.txt")) && File.Exists(Path.Combine(folder,"Licenses","Sonic-APACHE.txt"))));
            Test("voice keys sort by language then name",()=>{
                var voices=App.Voices(folder);
                var groups=new Dictionary<string,int>{{"en-GB",0},{"en-US",1},{"en",2},{"fr-FR",3},{"de-DE",4},{"pt-BR",5},{"es-ES",6},{"es-MX",7}};
                Func<string,string> sortName=name=>String.Concat(name.Normalize(System.Text.NormalizationForm.FormD).Where(c=>Char.IsLetterOrDigit(c))).ToLowerInvariant();
                var expected=voices.OrderBy(v=>groups[v.locale]).ThenBy(v=>sortName(v.name),StringComparer.Ordinal).Select(v=>v.name);
                Assert(voices.OrderBy(v=>v.id,StringComparer.OrdinalIgnoreCase).Select(v=>v.name).SequenceEqual(expected));
                Assert(voices.All(v=>v.ToString().StartsWith(v.name+" (")));
            });
            Test("secure system TLS defaults configured",()=>{
                var config=File.ReadAllText(Path.Combine(folder,"PhononSAPI.exe.config"));
                Assert(config.Contains("DontEnableSystemDefaultTlsVersions=false") && config.Contains("DontEnableSchUseStrongCrypto=false"));
            });
            Test("release manual has product and support links without testing history",()=>{
                string manual=File.ReadAllText(Path.Combine(folder,"Manual.html"));
                foreach(string url in new[]{"https://github.com/OnjLouis/phononSAPI/releases/latest", "https://github.com/OnjLouis/phononNVDA", "https://github.com/OnjLouis/phononMac/releases/latest", "https://onj.me/software", "https://onj.me/donate"})Assert(manual.Contains("href=\""+url+"\""));
                Assert(!manual.Contains("testing candidate") && !manual.Contains("0.1.0") && !manual.Contains("0.1.1"));
                Assert(System.Text.RegularExpressions.Regex.Matches(manual,@"\bNVDA\b").Count==1);
                Assert(manual.Split(new[]{"<h2>Credits And Licences</h2>"},StringSplitOptions.None).Length==2);
            });
            Console.WriteLine("Usability regressions failed: "+failures);return failures==0?0:1;
        }
    }
}
