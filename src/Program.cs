using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace FlareIdle {
 internal static class Program {
  internal static string SessionPath(string prefix,string session) {
   if(!Regex.IsMatch(session??"","^[0-9a-f]{32}$"))throw new ArgumentException("invalid session identity");
   return Path.Combine(Config.DataRoot,prefix+"-"+session+".json");
  }
  [STAThread] static int Main(string[] args) {
   try {
    if(args.Length==5&&args[0]=="--engine") {var config=Storage.Load<Config>(SessionPath("session",args[4]));config.Validate();new Engine(config,Int32.Parse(args[1]),Int64.Parse(args[2]),args[3],args[4]).Run();return 0;}
    if(args.Length==4&&args[0]=="--guardian") {Guardian(Int32.Parse(args[1]),Int64.Parse(args[2]),args[3]);return 0;}
    if(args.Length==1&&args[0]=="--recover") {
     if(!Identity.Admin){using(var p=Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,"--recover"){UseShellExecute=true,Verb="runas"})){p.WaitForExit();return p.ExitCode;}}
     using(var guard=new Mutex(false,"Local\\FlareIdle.Engine")) {
      bool owned=false;try{owned=guard.WaitOne(0);}catch(AbandonedMutexException){owned=true;}
      if(!owned)return 2;
      try{var prior=Json.ReadFile<Status>(new Config().StatePath);if(!prior.SafeToClose)Guardian(0,0,prior.SessionId);return Json.ReadFile<Status>(new Config().StatePath).SafeToClose?0:2;}
      finally{guard.ReleaseMutex();}
     }
    }
    if(args.Length==2&&args[0]=="--probe") {File.WriteAllText(args[1],Json.Write(Gpu.Enumerate()));return 0;}
    if(args.Length==2&&args[0]=="--preview-png") {
     var window=new Gui(true);window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));var view=(FrameworkElement)window.Content;
     view.Measure(new Size(570,720));view.Arrange(new Rect(0,0,570,720));view.UpdateLayout();
     var bitmap=new RenderTargetBitmap(570,720,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
     var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(args[1]))encoder.Save(file);return 0;
    }
    if(args.Length==1&&args[0]=="--headless") {CoreClient.Start();Thread.Sleep(1500);while(CoreClient.Snapshot().WorkerAlive)Thread.Sleep(500);return CoreClient.Snapshot().PowerRestored&&CoreClient.Snapshot().Error==null?0:2;}
    if(args.Length==2&&args[0]=="--control") {CoreClient.Send(args[1]);return 0;}
    using(var singleton=new Mutex(false,"Local\\FlareIdle.Gui")) {
     bool own=false;try{own=singleton.WaitOne(0);}catch(AbandonedMutexException){own=true;}if(!own)return 0;
     try{var app=new Application();app.Run(new Gui(false));}finally{singleton.ReleaseMutex();}
    }
    return 0;
   }catch(Exception e){try{Storage.SecureDirectory(Config.DataRoot);File.WriteAllText(Path.Combine(Config.DataRoot,"last-error.txt"),e.GetType().Name+": "+e.GetBaseException().Message);}catch{}return 1;}
  }
  static void Guardian(int enginePid,long engineTicks,string session) {
   var cfg=Storage.Load<Config>(SessionPath("session",session));var recovery=Storage.Load<Recovery>(SessionPath("recovery",session));
   using(var probe=new Gpu(cfg.GpuUuid)){probe.Read();}
   File.WriteAllText(Path.Combine(cfg.Root,"guardian-"+session+".ready"),"ready");
   bool stale=false;DateTime last=DateTime.UtcNow;
   while(Identity.Alive(enginePid,engineTicks)) {
    try{var state=Json.ReadFile<Status>(SessionPath("status",session));if(state.SessionId!=session)return;if(state.SafeToClose)return;last=DateTime.Parse(state.UpdatedUtc).ToUniversalTime();}catch{}
    if((DateTime.UtcNow-last).TotalSeconds>15){stale=true;break;}Thread.Sleep(1000);
   }
   if(stale&&Identity.Alive(enginePid,engineTicks))using(var process=Process.GetProcessById(enginePid)) {
    if(!String.Equals(process.MainModule.FileName,Process.GetCurrentProcess().MainModule.FileName,StringComparison.OrdinalIgnoreCase))return;
    process.Kill();if(!process.WaitForExit(5000))return;
   }
   Thread.Sleep(1000);Status final;
   try{final=Json.ReadFile<Status>(SessionPath("status",session));if(final.SafeToClose)return;}catch{return;}
   using(var guard=new Mutex(false,"Local\\FlareIdle.Gpu."+cfg.GpuUuid)) {
    bool owned=false;try{owned=guard.WaitOne(3000);}catch(AbandonedMutexException){owned=true;}if(!owned)return;
    try {
     foreach(var name in new[]{Path.GetFileNameWithoutExtension(cfg.FlareExecutable),Path.GetFileNameWithoutExtension(cfg.MinerExecutable)}.Where(n=>!String.IsNullOrWhiteSpace(n)))
      foreach(var process in Process.GetProcessesByName(name))using(process)if(!process.HasExited)throw new IOException("a worker remains; power restoration withheld");
     using(var gpu=new Gpu(cfg.GpuUuid)){if(recovery.RestoreRequired){if(!recovery.PreviousPowerLimitWatts.HasValue)throw new IOException("saved power limit unavailable");gpu.Restore(recovery.PreviousPowerLimitWatts.Value);}StatusTelemetry.UpdateGpu(final,gpu.Read());}
     try{File.Delete(Path.Combine(cfg.Root,"api-key.txt"));}catch{}
     StatusTelemetry.WorkersStopped(final);final.PowerRestored=true;final.SafeToClose=true;final.Phase="error";
     final.Error="controller exited; guardian stopped workers and restored the saved power cap";final.Detail="safe to close; inspect settings before restarting";
    }catch(Exception e){final.Phase="recovery required";final.Error=e.GetBaseException().Message;final.SafeToClose=false;}
    finally{final.WorkerAlive=false;final.UpdatedUtc=DateTime.UtcNow.ToString("o");try{Json.Atomic(SessionPath("status",session),final);var latest=Json.ReadFile<Status>(cfg.StatePath);if(latest.SessionId==session)Json.Atomic(cfg.StatePath,final);}catch{}guard.ReleaseMutex();}
   }
  }
 }
}
