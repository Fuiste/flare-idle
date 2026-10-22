using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FlareIdle {
    internal static class Identity {
        public static bool Alive(int pid,long ticks) { try { using(var p=Process.GetProcessById(pid)) return !p.HasExited && p.StartTime.ToUniversalTime().Ticks==ticks; } catch { return false; } }
        public static long Ticks(Process p) { return p.StartTime.ToUniversalTime().Ticks; }
        public static bool Admin { get { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } }
    }
    internal static class StatusTelemetry {
        internal static void WorkersStopped(Status state) {
            state.ActiveRequests=0;state.PendingRequests=0;state.TokensPerSecond=0;state.HashrateTHs=0;
            state.FlarePid=0;state.MinerPid=0;
        }
        internal static void ClearLoad(Status state) {
            state.PowerWatts=null;state.TemperatureC=null;state.MemoryUsedGiB=null;
        }
        internal static void UpdateGpu(Status state,GpuReading reading) {
            state.PowerWatts=reading.PowerWatts;state.PowerLimitWatts=reading.PowerLimitWatts;
            state.TemperatureC=reading.TemperatureC;state.MemoryUsedGiB=reading.MemoryUsedGiB;
            state.MemoryTotalGiB=reading.MemoryTotalGiB;
        }
    }
    public static class CoreClient {
        static readonly object sync=new object();
        static string pipe,token; static Process worker; static Status current=new Status {Phase="stopped",Detail="ready to start",SafeToClose=true};
        public static void Start() {
            lock(sync) {
                if(worker!=null && !worker.HasExited) return;
                var config=Config.Load();config.Validate();Storage.SecureDirectory(config.Root);
                if(File.Exists(config.StatePath)&&!Json.ReadFile<Status>(config.StatePath).SafeToClose)
                    throw new InvalidOperationException("previous session recovery is incomplete; wait for its guardian or run FlareIdle.exe --recover before restarting");
                pipe="flare-idle-"+Guid.NewGuid().ToString("N"); token=Guid.NewGuid().ToString("N");
                using(var me=Process.GetCurrentProcess()) {
                    current=new Status {Phase="starting",Detail="waiting for windows permission",WorkerAlive=true};
                    Storage.Save(Path.Combine(config.Root,"connection-"+token+".json"),new[]{pipe,token});
                    Storage.Save(Path.Combine(config.Root,"session-"+token+".json"),config);
                    var start=new ProcessStartInfo(me.MainModule.FileName,"--engine "+me.Id+" "+Identity.Ticks(me)+" "+pipe+" "+token) { UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden };
                    if(!Identity.Admin) start.Verb="runas";
                    try { worker=Process.Start(start); }
                    catch(Exception) { current=new Status {Phase="error",Error="windows permission was not granted",Detail="no workloads started",SafeToClose=true}; }
                }
            }
        }
        public static void Send(string action) {
            string p,t; lock(sync) {p=pipe;t=token;}
            if(p==null) {
                var s=Json.ReadFile<Status>(new Config().StatePath);
                if(s==null||String.IsNullOrEmpty(s.SessionId))throw new InvalidDataException("no active controller session");
                var info=Storage.Load<string[]>(Path.Combine(new Config().Root,"connection-"+s.SessionId+".json"));
                if(info==null||info.Length!=2)throw new InvalidDataException("controller connection information is invalid");
                p=info[0];t=info[1];
            }
            using(var c=new NamedPipeClientStream(".",p,PipeDirection.InOut)) {
                c.Connect(3000);
                var writer=new StreamWriter(c,new UTF8Encoding(false)) {AutoFlush=true};
                var reader=new StreamReader(c,Encoding.UTF8);
                writer.WriteLine(t+" "+action); if(reader.ReadLine()!="ok")throw new IOException("controller rejected the command");
            }
        }
        public static Status Snapshot() {
            lock(sync) {
                if(worker!=null) {
                    try { var s=Json.ReadFile<Status>(Path.Combine(new Config().Root,"status-"+token+".json")); if(s.SessionId==token) current=s; } catch {}
                    try {
                        bool running=!worker.HasExited;
                        if(!running&&!File.Exists(Path.Combine(new Config().Root,"status-"+token+".json")))
                            current=new Status {Phase="error",Error="controller did not start; no workloads were launched",SafeToClose=true,PowerRestored=true};
                        current.WorkerAlive=running||!current.SafeToClose;
                    } catch { current.WorkerAlive=!current.SafeToClose; }
                }
                return Json.Read<Status>(Json.Write(current));
            }
        }
    }
    internal sealed class Worker : IDisposable {
        readonly OwnedJob job=new OwnedJob(); readonly NativeOutputCapture capture=new NativeOutputCapture(); readonly Action<string> log;
        public int Id { get { return job.Host.Id; } }
        public bool Exited { get { return job.Host.Exited; } }
        public Worker(string exe,string[] args,Action<string> log) {
            this.log=log;
            try {
                job.CreateCaptured(exe,args,capture.WriteHandle);
                capture.CloseWriter();
                Task.Run((Action)ReadLog);
                job.Host.Resume();
            } catch { Dispose(); throw; }
        }
        void ReadLog() { try { using(var r=new StreamReader(capture.Input)) { string line; while((line=r.ReadLine())!=null) if(log!=null) log(line); } } catch {} }
        public void Stop() { if(!job.WaitEmpty(0)) job.Kill(); if(!job.WaitEmpty(5000)||!job.Host.Wait(5000)) throw new IOException("worker did not exit; power restoration withheld"); }
        public void Dispose() { job.Dispose(); capture.Dispose(); }
    }
    internal sealed class Engine {
        readonly Config cfg; readonly int parentPid; readonly long parentTicks; readonly string pipe,token;
        readonly object stateLock=new object(),logLock=new object(),minerLock=new object(); readonly Status state; readonly List<Mutex> guards=new List<Mutex>();
        readonly HttpClient http=new HttpClient(new HttpClientHandler {UseProxy=false}) {Timeout=TimeSpan.FromMilliseconds(800)};
        readonly WorkGate gate; volatile bool stopping,ready,allowMining=true; volatile string fault;
        Worker flare,miner; Gpu gpu; Gateway gateway; bool powerOwned,ownsState; Recovery recovery;
        DateTime metricsAt=DateTime.MinValue,minerStart=DateTime.MinValue,minerTelemetryAt=DateTime.MinValue;
        long sessionAccepted,sessionRejected,priorAccepted,priorRejected,lastTokens; int nativeActive,nativePending; DateTime tokenAt=DateTime.MinValue;
        string apiKey,modelPackage,publisher,trust,modelName,minerExe,miningUser,minerDevice; string[] pools;
        public Engine(Config cfg,int parentPid,long parentTicks,string pipe,string token) {
            this.cfg=cfg;this.parentPid=parentPid;this.parentTicks=parentTicks;this.pipe=pipe;this.token=token;
            allowMining=cfg.MinerEnabled;
            state=new Status {SessionId=token,Phase="starting",Detail="checking gpu ownership",MiningAllowed=allowMining,WorkerAlive=true,ModelAlias=cfg.ModelAlias,MinerCoin=cfg.MinerCoin};
            gate=new WorkGate(StopMinerAsync,()=>ready&&!stopping&&fault==null);
        }
        public void Run() {
            int exit=0;
            try {
                Acquire("Local\\FlareIdle.Engine");
                ownsState=true; SecureLogs(); Pulse();
                Acquire("Local\\FlareIdle.Gpu."+cfg.GpuUuid);
                CheckConflicts(); LoadSettings();
                StartPipe();
                gpu=new Gpu(cfg.GpuUuid);var initial=gpu.Read();
                if(initial.ComputeMajor.HasValue&&(initial.ComputeMajor.Value!=12||!(initial.ComputeMinor==0||initial.ComputeMinor==1)))throw new InvalidOperationException("current flare requires a compatible blackwell sm120 or sm121 gpu");
                if(cfg.MinerEnabled)minerDevice=LaunchArguments.ShortPci(initial.PciBusId);
                recovery=new Recovery {GpuUuid=cfg.GpuUuid,PreviousPowerLimitWatts=cfg.PowerWatts>0?(double?)gpu.CaptureBaseline():null,RestoreRequired=cfg.PowerWatts>0};
                Storage.Save(Path.Combine(cfg.Root,"recovery-"+token+".json"),recovery);
                StartGuardian();
                if(cfg.PowerWatts>0){powerOwned=true;gpu.ApplyCap(cfg.PowerWatts);}
                Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES",cfg.GpuUuid);
                ReadGpu();
                StartFlare();
                if(!stopping) {
                    gateway=new Gateway("127.0.0.1",cfg.Port,"http://127.0.0.1:"+cfg.BackendPort,apiKey,gate.Enter,()=>ready&&!stopping&&fault==null);
                    gateway.Start(); ready=true; state.ApiReady=true; gate.AllowInitialIdle(TimeSpan.FromSeconds(cfg.IdleDelaySeconds));
                    Log("inference ready");
                }
                while(!stopping) {
                    if(!Identity.Alive(parentPid,parentTicks)) {stopping=true;break;}
                    if(fault!=null) throw new InvalidOperationException(fault);
                    if(flare.Exited) throw new InvalidOperationException("flare worker exited unexpectedly; restart required");
                    ReadGpu(); PollFlare(); PollMiner();
                    gate.TryStart(CanMine,StartMiner).GetAwaiter().GetResult();
                    lock(stateLock) {
                        state.Phase=gate.Active>0||nativeActive>0||nativePending>0?"inference":miner!=null?"mining":"ready";
                        state.Detail=state.Phase=="inference"?"model serving; mining has yielded":miner!=null?"model resident; mining uses idle time":allowMining?"model resident; waiting for idle cooldown":"model resident; mining paused or disabled";
                        state.ActiveRequests=gate.Active;state.PendingRequests=nativePending;
                    }
                    Pulse(); Thread.Sleep(500);
                }
            } catch(Exception e) { exit=1; fault=SafeMessage(e); Log("fault: "+fault); }
            finally {
                Log("shutdown: begin");
                stopping=true; ready=false;state.ApiReady=false;state.Phase="stopping";state.Detail="stopping workers before restoring gpu settings";Pulse();
                try {if(gateway!=null) gateway.Stop();}catch(Exception e){fault=SafeMessage(e);Log("shutdown: gateway stop failed: "+fault);}
                Log("shutdown: gateway closed");
                try { gate.Pause().GetAwaiter().GetResult(); } catch(Exception e) {fault=SafeMessage(e);exit=1;}
                Log("shutdown: miner stopped");
                Pulse();var wait=Stopwatch.StartNew(); while(gate.Active>0&&wait.ElapsedMilliseconds<10000) {Pulse();Thread.Sleep(100);}
                Log("shutdown: inference drain complete");
                bool clean=true;
                Pulse();try { if(flare!=null) {flare.Stop();flare.Dispose();flare=null;} } catch(Exception e) {clean=false;fault=SafeMessage(e);}
                Log("shutdown: flare stopped");
                try { if(miner!=null) {miner.Stop();miner.Dispose();miner=null;} } catch(Exception e) {clean=false;fault=SafeMessage(e);}
                if(clean){StatusTelemetry.WorkersStopped(state);StatusTelemetry.ClearLoad(state);}
                if(clean&&powerOwned) {
                    var settle=Stopwatch.StartNew();
                    for(;;)try {CheckConflicts();break;}catch(Exception e){
                        if(settle.ElapsedMilliseconds>=5000){clean=false;fault=SafeMessage(e);break;}
                        Pulse();Thread.Sleep(100);
                    }
                }
                if(clean&&powerOwned) try {gpu.Restore(recovery.PreviousPowerLimitWatts.Value);state.PowerRestored=true;StatusTelemetry.UpdateGpu(state,gpu.Read());} catch(Exception e) {clean=false;fault=SafeMessage(e);}
                if(clean&&!powerOwned)state.PowerRestored=true;
                Log("shutdown: restoration "+(clean?"verified":fault));
                state.Phase=clean?(exit==0?"stopped":"error"):"recovery required";
                state.Detail=clean?"workers stopped; "+(powerOwned?"previous power cap restored":"gpu settings unchanged"):"cleanup incomplete; use recovery before other gpu work";
                state.Error=fault;state.SafeToClose=clean;state.WorkerAlive=false;if(clean){state.FlarePid=0;state.MinerPid=0;}
                for(int publication=0;publication<3;publication++){if(Pulse())break;Thread.Sleep(100);}
                Log("shutdown: final state published");
                if(clean)try{File.Delete(Path.Combine(cfg.Root,"api-key.txt"));}catch{}
                if(gpu!=null) gpu.Dispose(); http.Dispose();
                for(int i=guards.Count-1;i>=0;i--) {try {guards[i].ReleaseMutex();}catch{} guards[i].Dispose();}
            }
        }
        string SafeMessage(Exception e) {
            string s=e.GetBaseException().Message;
            if(!String.IsNullOrEmpty(apiKey))s=s.Replace(apiKey,"[redacted]");
            if(!String.IsNullOrEmpty(miningUser))s=s.Replace(miningUser,"[worker]");
            if(!String.IsNullOrEmpty(cfg.MinerPassword))s=s.Replace(cfg.MinerPassword,"[redacted]");
            return s.Length>350?s.Substring(0,350):s;
        }
        void Acquire(string name) {
            bool created;var m=new Mutex(false,name,out created); bool held=false;
            try {held=m.WaitOne(0);} catch(AbandonedMutexException){held=true;}
            if(!held){m.Dispose();throw new InvalidOperationException("another gpu controller is active; close it and retry");} guards.Add(m);
        }
        void CheckConflicts() {
            var names=new[]{"krig-miner","flare","flare-worker","llama-server",Path.GetFileNameWithoutExtension(cfg.FlareExecutable),Path.GetFileNameWithoutExtension(cfg.MinerExecutable)}.Where(n=>!String.IsNullOrEmpty(n)).Distinct();
            foreach(string name in names)foreach(var p in Process.GetProcessesByName(name))using(p)if(!p.HasExited)throw new InvalidOperationException("another inference or mining worker is running; stop it before starting flare idle");
            foreach(var endpoint in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                if(endpoint.Port==cfg.BackendPort||endpoint.Port==cfg.MinerPort||endpoint.Port==cfg.Port)throw new InvalidOperationException("one of the configured local ports is in use");
        }
        void LoadSettings() {
            cfg.Validate();apiKey=cfg.ApiKey;modelPackage=cfg.ModelPackage;publisher=cfg.PublisherKeysFile;trust=cfg.TrustStoreFile;modelName=cfg.ModelAlias;
            minerExe=cfg.MinerExecutable;miningUser=cfg.MinerUser;pools=cfg.MinerPools;
            var self=Process.GetCurrentProcess().MainModule.FileName;
            if(String.Equals(cfg.FlareExecutable,self,StringComparison.OrdinalIgnoreCase)||String.Equals(cfg.MinerExecutable,self,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("choose the actual flare and miner programs");
            File.WriteAllText(Path.Combine(cfg.Root,"api-key.txt"),apiKey);
            state.Endpoint="http://127.0.0.1:"+cfg.Port+"/v1";
        }
        void SecureLogs() {
            Directory.CreateDirectory(cfg.LogDirectory);
            var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            Directory.SetAccessControl(cfg.LogDirectory,acl);
        }
        void Log(string line) {
            try {lock(logLock){
                if(!String.IsNullOrEmpty(apiKey))line=line.Replace(apiKey,"[redacted]");if(!String.IsNullOrEmpty(miningUser))line=line.Replace(miningUser,"[worker]");
                if(!String.IsNullOrEmpty(cfg.MinerPassword))line=line.Replace(cfg.MinerPassword,"[redacted]");
                string path=Path.Combine(cfg.LogDirectory,"session.log"); if(File.Exists(path)&&new FileInfo(path).Length>4*1024*1024)File.Move(path,path+"."+DateTime.UtcNow.Ticks);
                File.AppendAllText(path,DateTime.UtcNow.ToString("o")+" "+line+Environment.NewLine);
            }}catch{}
        }
        bool Pulse() {
            try {lock(stateLock){state.UpdatedUtc=DateTime.UtcNow.ToString("o");state.MiningAllowed=allowMining;state.IdleSeconds=Math.Max(0,(DateTime.UtcNow-gate.LastIdle).TotalSeconds);state.LastHandoffMs=gate.LastHandoffMs;state.RequestCount=gateway==null?0:gateway.RequestCount;Json.Atomic(Path.Combine(cfg.Root,"status-"+token+".json"),state);if(ownsState)Json.Atomic(cfg.StatePath,state);}return true;}catch(Exception e){stopping=true;fault="status publication failed";Log("status publication failed: "+SafeMessage(e));return false;}
        }
        void ReadGpu() {
            var g=gpu.Read();
            if(cfg.PowerWatts>0&&(!g.PowerLimitWatts.HasValue||Math.Abs(g.PowerLimitWatts.Value-cfg.PowerWatts)>.5))throw new InvalidOperationException("gpu power cap changed outside this app; stopping");
            if(!g.TemperatureC.HasValue)throw new InvalidOperationException("gpu temperature is unavailable; stopping");
            if(g.TemperatureC.Value>=cfg.CoreCutoffC)throw new InvalidOperationException("gpu reached the configured temperature cutoff");
            if(g.MemoryTemperatureC.HasValue&&g.MemoryTemperatureC.Value>=100)throw new InvalidOperationException("gpu memory reached the conservative cutoff");
            lock(stateLock){StatusTelemetry.UpdateGpu(state,g);state.GpuName=g.Name;}
        }
        void StartFlare() {
            state.Phase="loading";state.Detail="loading the selected model";Pulse();
            var args=LaunchArguments.Flare(cfg,Path.Combine(cfg.Root,"api-key.txt"));
            flare=new Worker(cfg.FlareExecutable,args,line=>{if(!ready)Log("flare: "+line);});state.FlarePid=flare.Id;
            var timer=Stopwatch.StartNew();
            while(!stopping&&timer.Elapsed.TotalSeconds<cfg.StartupSeconds) {
                if(!Identity.Alive(parentPid,parentTicks)){stopping=true;return;}
                if(flare.Exited)throw new InvalidOperationException("flare failed to load; check its compatible package and the private startup log");
                ReadGpu();Pulse();
                try {using(var r=http.GetAsync("http://127.0.0.1:"+cfg.BackendPort+"/health").Result)if(r.IsSuccessStatusCode)return;}catch{}
                Thread.Sleep(500);
            }
            if(!stopping)throw new TimeoutException("model startup timed out");
        }
        bool CanMine() {return cfg.MinerEnabled&&ready&&!stopping&&fault==null&&allowMining&&miner==null&&nativeActive==0&&nativePending==0&&metricsAt>gate.LastIdle&&(DateTime.UtcNow-metricsAt).TotalSeconds<3&&(DateTime.UtcNow-gate.LastIdle).TotalSeconds>=cfg.IdleDelaySeconds;}
        void StartMiner() {
            lock(minerLock) {
            ReadGpu(); var args=LaunchArguments.Miner(cfg,minerDevice);
            miner=new Worker(minerExe,args,line=>{});minerStart=DateTime.UtcNow;minerTelemetryAt=minerStart;sessionAccepted=0;sessionRejected=0;state.MinerPid=miner.Id;Log("idle mining started");
            }
        }
        Task StopMinerAsync() {
            lock(minerLock) {
            try {
            if(miner!=null) {
                var timer=Stopwatch.StartNew();miner.Stop();miner.Dispose();miner=null;
                priorAccepted+=sessionAccepted;priorRejected+=sessionRejected;state.MinerPid=0;state.HashrateTHs=null;
                Log("mining yielded in "+timer.Elapsed.TotalMilliseconds.ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+" ms");
            }
            return Task.FromResult(0);
            }catch(Exception e){fault=SafeMessage(e);stopping=true;throw;}
            }
        }
        void PollFlare() {
            try {
                var d=Json.Read<Dictionary<string,object>>(http.GetStringAsync("http://127.0.0.1:"+cfg.BackendPort+"/metrics?format=json").Result);
                if(!d.ContainsKey("active")||!d.ContainsKey("pending")||Int(d,"active")<0||Int(d,"pending")<0)throw new InvalidDataException("unrecognized flare telemetry");
                nativeActive=Int(d,"active");nativePending=Int(d,"pending");metricsAt=DateTime.UtcNow;
                if(nativeActive>0||nativePending>0)gate.Pause().GetAwaiter().GetResult();
                long n=Long(d,"generated_tokens");if(tokenAt!=DateTime.MinValue)state.TokensPerSecond=Math.Max(0,(n-lastTokens)/(metricsAt-tokenAt).TotalSeconds);lastTokens=n;tokenAt=metricsAt;
            } catch {gate.Pause().GetAwaiter().GetResult();if((DateTime.UtcNow-metricsAt).TotalSeconds>10)throw new IOException("flare telemetry unavailable; mining stopped");}
        }
        void PollMiner() {
            Worker observed;lock(minerLock){observed=miner;if(observed==null)return;if(observed.Exited)throw new IOException("miner exited unexpectedly; restart required");}
            Dictionary<string,object> d=null;
            try {d=Json.Read<Dictionary<string,object>>(http.GetStringAsync("http://127.0.0.1:"+cfg.MinerPort).Result);}catch{}
            // No socket operation holds the admission lock. Old responses are
            // discarded after a stop/restart instead of touching the next run.
            lock(minerLock) {
                if(!Object.ReferenceEquals(observed,miner))return;
                try {
                    if(d==null)throw new IOException("mining metrics unavailable");
                    var devices=(System.Collections.IEnumerable)d["devices"];int observedAlgorithms=0;
                    foreach(Dictionary<string,object> dev in devices)foreach(Dictionary<string,object> a in (System.Collections.IEnumerable)dev["algorithms"]){
                        double rate=Convert.ToDouble(a["hashrate"]);if(Double.IsNaN(rate)||Double.IsInfinity(rate)||rate<0)throw new InvalidDataException("invalid mining rate");state.HashrateHps=rate;var shares=(Dictionary<string,object>)a["shares"];
                        if(!shares.ContainsKey("accepted")||!shares.ContainsKey("rejected"))throw new IOException("incomplete mining counters");
                        long accepted=Long(shares,"accepted"),rejected=Long(shares,"rejected");
                        if(accepted<0||rejected<0)throw new InvalidDataException("invalid mining counters");observedAlgorithms++;
                        sessionRejected=Math.Max(sessionRejected,rejected);sessionAccepted=Math.Max(sessionAccepted,accepted);
                    }
                    if(observedAlgorithms!=1)throw new IOException("expected one mining algorithm");
                    minerTelemetryAt=DateTime.UtcNow;
                }catch(InvalidDataException){throw;}catch{state.HashrateHps=null;if((DateTime.UtcNow-minerTelemetryAt).TotalSeconds>30)throw new IOException("miner telemetry stale; restart required");}
                state.AcceptedShares=priorAccepted+sessionAccepted;state.RejectedShares=priorRejected+sessionRejected;
            }
        }
        static int Int(Dictionary<string,object>d,string k){object v;return d.TryGetValue(k,out v)?Convert.ToInt32(v):0;}
        static long Long(Dictionary<string,object>d,string k){object v;return d.TryGetValue(k,out v)?Convert.ToInt64(v):0;}
        void StartGuardian() {
            Process p;using(var me=Process.GetCurrentProcess())p=Process.Start(new ProcessStartInfo(me.MainModule.FileName,"--guardian "+me.Id+" "+Identity.Ticks(me)+" "+token) {UseShellExecute=false,CreateNoWindow=true});
            using(p){var timer=Stopwatch.StartNew();while(timer.ElapsedMilliseconds<5000){if(p.HasExited)throw new InvalidOperationException("guardian failed to start");if(File.Exists(Path.Combine(cfg.Root,"guardian-"+token+".ready")))return;Thread.Sleep(50);}throw new TimeoutException("guardian readiness was not confirmed");}
        }
        void StartPipe() {
            Task.Run((Action)delegate {
                while(!stopping)try {
                    var security=new PipeSecurity();security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,PipeAccessRights.FullControl,AccessControlType.Allow));
                    using(var server=new NamedPipeServerStream(pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.None,4096,4096,security)) {
                        server.WaitForConnection();var r=new StreamReader(server,Encoding.UTF8);var w=new StreamWriter(server,new UTF8Encoding(false)){AutoFlush=true};
                        string line=r.ReadLine();bool ok=line!=null&&line.StartsWith(token+" ",StringComparison.Ordinal);
                        if(ok){string action=line.Substring(token.Length+1);if(action=="stop")stopping=true;else if(action=="pause-mining"){allowMining=false;gate.Pause().GetAwaiter().GetResult();}else if(action=="resume-mining"&&cfg.MinerEnabled)allowMining=true;else ok=false;}
                        w.WriteLine(ok?"ok":"rejected");
                    }
                }catch(Exception e){if(!stopping){fault=SafeMessage(e);stopping=true;}}
            });
        }
    }
}

