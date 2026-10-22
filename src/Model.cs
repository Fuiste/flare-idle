using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace FlareIdle {
    public sealed class Status {
        public string SessionId {get;set;} public string UpdatedUtc {get;set;}
        public string Phase {get;set;} public string Detail {get;set;} public string Error {get;set;}
        public string Endpoint {get;set;} public string ModelAlias {get;set;} public string MinerCoin {get;set;} public string GpuName {get;set;}
        public bool MiningAllowed {get;set;} public bool ApiReady {get;set;}
        public double? PowerWatts {get;set;} public double? PowerLimitWatts {get;set;}
        public double? TemperatureC {get;set;} public double? MemoryUsedGiB {get;set;} public double? MemoryTotalGiB {get;set;}
        public double? HashrateHps {get;set;} public double? HashrateTHs {get {return HashrateHps/1e12;} set {HashrateHps=value*1e12;}}
        public double? TokensPerSecond {get;set;} public int ActiveRequests {get;set;} public int PendingRequests {get;set;}
        public long AcceptedShares {get;set;} public long RejectedShares {get;set;}
        public int FlarePid {get;set;} public int MinerPid {get;set;} public double IdleSeconds {get;set;}
        public bool WorkerAlive {get;set;} public bool SafeToClose {get;set;} public bool PowerRestored {get;set;}
        public double LastHandoffMs {get;set;} public long RequestCount {get;set;}
    }
    public sealed class Config {
        public int SchemaVersion=1;
        public string FlareExecutable="",ModelPackage="",PublisherKeysFile="",TrustStoreFile="",ApiKey="",ModelAlias="local-model",GpuUuid="";
        public int PowerWatts=0,ReserveGiB=2,MaxConcurrency=1,MtpDraftTokens=0,IdleDelaySeconds=2,Port=8000,CoreCutoffC=80;
        public bool MinerEnabled=false;
        public string MinerExecutable="",MinerCoin="pearl",MinerUser="",MinerPassword="";
        public string[] MinerPools=new string[0];
        public int BackendPort=18080,MinerPort=12080,StartupSeconds=900;
        [ScriptIgnore] public static string FilePath {get {return Path.Combine(DataRoot,"config.json");}}
        public static string DataRoot {get {return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FlareIdle");}}
        [ScriptIgnore] public string Root {get {return DataRoot;}}
        [ScriptIgnore] public string StatePath {get {return Path.Combine(Root,"status.json");}}
        [ScriptIgnore] public string LogDirectory {get {return Path.Combine(Root,"logs");}}
        [ScriptIgnore] public bool IsConfigured {get {try {Validate();return true;}catch{return false;}}}
        public static Config Load() {return File.Exists(FilePath)?Storage.Load<Config>(FilePath):new Config();}
        public Config Clone() {return Json.Read<Config>(Json.Write(this));}
        public void Save() {Validate();Storage.Save(FilePath,this);}
        public void Validate() {
            if(SchemaVersion!=1)throw new InvalidDataException("unsupported settings version");
            FilePathRequired(FlareExecutable,"flare executable");FilePathRequired(ModelPackage,"prepared flare model package");
            if(!FlareExecutable.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("select a windows flare executable");
            if(!ModelPackage.EndsWith(".safetensors",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("select a prepared flare .safetensors package");
            if(!String.IsNullOrWhiteSpace(PublisherKeysFile))FilePathRequired(PublisherKeysFile,"publisher keys");
            if(!String.IsNullOrWhiteSpace(TrustStoreFile)&&(!Path.IsPathRooted(TrustStoreFile)||!Directory.Exists(Path.GetDirectoryName(TrustStoreFile))))throw new InvalidDataException("select a trust store in an existing directory");
            if(String.IsNullOrWhiteSpace(GpuUuid)||!Regex.IsMatch(GpuUuid,@"^GPU-[a-fA-F0-9-]{8,64}$"))throw new InvalidDataException("select an nvidia gpu");
            if(String.IsNullOrWhiteSpace(ApiKey)||ApiKey.Length<16||ApiKey.Length>512||ApiKey.Any(Char.IsWhiteSpace))throw new InvalidDataException("api key must contain 16 to 512 non-space characters");
            if(String.IsNullOrWhiteSpace(ModelAlias)||ModelAlias.Length>100||ModelAlias.Any(Char.IsControl))throw new InvalidDataException("enter a model alias of 1 to 100 characters");
            if(PowerWatts<0||PowerWatts>2000)throw new InvalidDataException("power cap must be 0 (unchanged) or a device-supported wattage");
            if(ReserveGiB<1||ReserveGiB>256||MaxConcurrency<1||MaxConcurrency>64||MtpDraftTokens<0||MtpDraftTokens>16||IdleDelaySeconds<1||IdleDelaySeconds>300||CoreCutoffC<50||CoreCutoffC>85||StartupSeconds<30||StartupSeconds>1800)throw new InvalidDataException("settings exceed supported application limits");
            if(new[]{Port,BackendPort,MinerPort}.Any(p=>p<1024||p>65535)||new[]{Port,BackendPort,MinerPort}.Distinct().Count()!=3)throw new InvalidDataException("api and private ports must be distinct and between 1024 and 65535");
            if(MinerEnabled) {
                FilePathRequired(MinerExecutable,"krig miner executable");
                if(!MinerExecutable.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("select a windows krig miner executable");
                if(String.IsNullOrWhiteSpace(MinerCoin)||!Regex.IsMatch(MinerCoin,"^[a-zA-Z0-9_-]{1,32}$"))throw new InvalidDataException("enter a valid krig coin name");
                if(String.IsNullOrWhiteSpace(MinerUser)||MinerUser.Length>512||MinerUser.Any(Char.IsControl))throw new InvalidDataException("enter the pool's wallet or username/worker value");
                if(MinerPassword==null||MinerPassword.Length>512||MinerPassword.Any(Char.IsControl))throw new InvalidDataException("pool password must be at most 512 characters without control characters");
                if(MinerPools==null||MinerPools.Length<1||MinerPools.Length>8)throw new InvalidDataException("enter one to eight pool addresses");
                foreach(var pool in MinerPools) {
                    Uri u;if(!Uri.TryCreate(pool,UriKind.Absolute,out u)||!(u.Scheme=="stratum+ssl"||u.Scheme=="stratum+tcp")||String.IsNullOrEmpty(u.Host)||u.Port<1||!String.IsNullOrEmpty(u.UserInfo)||!String.IsNullOrEmpty(u.Query)||!String.IsNullOrEmpty(u.Fragment)||pool.Any(Char.IsWhiteSpace))throw new InvalidDataException("pool addresses must use stratum+ssl://host:port or stratum+tcp://host:port; put wallet/worker in its own field");
                }
            }
        }
        static void FilePathRequired(string path,string label){if(String.IsNullOrWhiteSpace(path)||!Path.IsPathRooted(path)||!File.Exists(path))throw new FileNotFoundException("select an existing "+label);}
    }
    public sealed class Recovery {
        public string GpuUuid;public double? PreviousPowerLimitWatts;public bool RestoreRequired;
    }
    internal static class Storage {
        static readonly byte[] entropy=Encoding.UTF8.GetBytes("flare-idle/settings/v1");
        public static void SecureDirectory(string path) {
            Directory.CreateDirectory(path);var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
            foreach(var sid in new[]{WindowsIdentity.GetCurrent().User,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            Directory.SetAccessControl(path,acl);
        }
        public static void Save(string path,object value) {
            SecureDirectory(Path.GetDirectoryName(path));
            var bytes=ProtectedData.Protect(Encoding.UTF8.GetBytes(Json.Write(value)),entropy,DataProtectionScope.CurrentUser);
            Json.Atomic(path,new Envelope {Version=1,ProtectedData=Convert.ToBase64String(bytes)});
        }
        public static T Load<T>(string path) {
            var value=Json.ReadFile<Envelope>(path);if(value==null||value.Version!=1)throw new InvalidDataException("unsupported protected settings");
            return Json.Read<T>(Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value.ProtectedData),entropy,DataProtectionScope.CurrentUser)));
        }
        public sealed class Envelope {public int Version;public string ProtectedData;}
    }
    internal static class Json {
        public static string Write(object value){return new JavaScriptSerializer {MaxJsonLength=32*1024*1024}.Serialize(value);}
        public static T Read<T>(string value){return new JavaScriptSerializer {MaxJsonLength=32*1024*1024}.Deserialize<T>(value);}
        public static T ReadFile<T>(string path){for(int n=0;;n++){try{using(var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var r=new StreamReader(f))return Read<T>(r.ReadToEnd());}catch(IOException){if(n>=9)throw;System.Threading.Thread.Sleep(20);}catch(UnauthorizedAccessException){if(n>=9)throw;System.Threading.Thread.Sleep(20);}}}
        public static void Atomic(string path,object value){string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";File.WriteAllText(tmp,Write(value));for(int n=0;;n++){try{if(File.Exists(path))File.Replace(tmp,path,null);else File.Move(tmp,path);return;}catch(Exception e){if(!(e is IOException||e is UnauthorizedAccessException)||n>=49){try{File.Delete(tmp);}catch{}throw;}System.Threading.Thread.Sleep(20);}}}
    }
}
