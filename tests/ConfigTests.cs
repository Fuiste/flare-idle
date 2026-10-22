using System;
using System.IO;
using System.Linq;
using FlareIdle;
internal static class ConfigTests {
 static int passed;
 static void Check(bool value,string name){if(!value)throw new Exception(name);passed++;Console.WriteLine("ok: "+name);}
 static void Reject(Action action,string name){try{action();}catch{Check(true,name);return;}throw new Exception("accepted invalid input: "+name);}
 static int Main(){
  string root=Path.Combine(Path.GetTempPath(),"flare-idle-config-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  string flare=Path.Combine(root,"runtime.exe"),model=Path.Combine(root,"model.safetensors"),miner=Path.Combine(root,"miner.exe"),saved=Path.Combine(root,"protected.json");
  foreach(var f in new[]{flare,model,miner})File.WriteAllText(f,"");
  try {
   var defaults=new Config();Check(defaults.PowerWatts==0&&!defaults.MinerEnabled&&!defaults.IsConfigured,"first launch has no implicit workload or power change");
   var c=new Config {FlareExecutable=flare,ModelPackage=model,GpuUuid="GPU-00000000-0000-0000-0000-000000000000",ApiKey=Guid.NewGuid().ToString("N")};
   c.Validate();Check(c.IsConfigured,"inference-only configuration needs no miner");
   var bad=c.Clone();bad.Port=bad.BackendPort;Reject(bad.Validate,"ports cannot overlap");
   bad=c.Clone();bad.ApiKey="short";Reject(bad.Validate,"short api key rejected");
   bad=c.Clone();bad.PowerWatts=-1;Reject(bad.Validate,"negative power cap rejected");
   bad=c.Clone();bad.MinerEnabled=true;Reject(bad.Validate,"mining requires explicit executable and pool identity");
   c.MinerEnabled=true;c.MinerExecutable=miner;c.MinerUser="example-worker";c.MinerPassword=Guid.NewGuid().ToString("N");c.MinerPools=new[]{"stratum+ssl://pool.example:3333"};c.Validate();
   Storage.Save(saved,c);var text=File.ReadAllText(saved);Check(!text.Contains(c.ApiKey)&&!text.Contains(c.MinerPassword)&&!text.Contains(c.MinerUser),"saved settings never contain plaintext credentials");
   var restored=Storage.Load<Config>(saved);Check(restored.ApiKey==c.ApiKey&&restored.MinerPassword==c.MinerPassword,"protected settings round trip for current user");
   var args=LaunchArguments.Miner(c,"00000000:2a:00.0");Check(args.Contains("--devices-pci")&&args.Contains("2a:00.0")&&args.Contains(c.MinerPassword),"miner binds selected pci device and passes optional pool password");
   Check(!args.Contains("--power-limit")&&!args.Contains("--gpu-core-offset"),"miner command does not request tuning");
   Reject(()=>LaunchArguments.ShortPci("00000001:2a:00.0"),"unsupported pci domain rejected");
   bad=c.Clone();bad.MinerPools=new[]{"stratum+ssl://user:password@pool.example:3333"};Reject(bad.Validate,"embedded pool credentials rejected");
   bad=c.Clone();bad.MinerCoin="pearl; launch";Reject(bad.Validate,"coin cannot contain command syntax");
   var fargs=LaunchArguments.Flare(c,Path.Combine(root,"key.txt"));Check(fargs.Contains(c.ModelPackage)&&fargs.Contains("127.0.0.1")&&!fargs.Contains(c.ApiKey),"flare command uses configured model, loopback and key-file reference");
   Check(!File.ReadAllText(saved).Contains(flare),"profile paths are also encrypted");
   Console.WriteLine(passed+" config checks passed");return 0;
  }catch(Exception e){Console.Error.WriteLine(e);return 1;}
  finally{foreach(var f in new[]{flare,model,miner,saved})try{File.Delete(f);}catch{}try{Directory.Delete(root);}catch{}}
 }
}
