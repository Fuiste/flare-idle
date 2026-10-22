using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FlareIdle {
 internal static class LaunchArguments {
  public static string[] Flare(Config config,string keyFile) {
   var args=new List<string>{"run",config.ModelPackage,"--gpus","0","--max-concurrency",config.MaxConcurrency.ToString(),"--mtp-draft-tokens",config.MtpDraftTokens.ToString(),"--host","127.0.0.1","--port",config.BackendPort.ToString(),"--served-model-name",config.ModelAlias,"--api-key-file",keyFile,"--memory-limit=-"+config.ReserveGiB+"GiB"};
   if(!String.IsNullOrWhiteSpace(config.PublisherKeysFile)){args.Add("--publisher-keys");args.Add(config.PublisherKeysFile);}
   if(!String.IsNullOrWhiteSpace(config.TrustStoreFile)){args.Add("--trust-store");args.Add(config.TrustStoreFile);}
   return args.ToArray();
  }
  public static string ShortPci(string value) {
   var match=Regex.Match(value??"",@"^(?:0{4,8}:)?([a-fA-F0-9]{2}:[a-fA-F0-9]{2}\.[0-7])$");
   if(!match.Success)throw new InvalidOperationException("mining requires a supported gpu pci address in domain zero");
   return match.Groups[1].Value.ToLowerInvariant();
  }
  public static string[] Miner(Config config,string pci) {
   var args=new List<string>{"--coin",config.MinerCoin};
   foreach(var pool in config.MinerPools){args.Add("--url");args.Add(pool);}
   args.AddRange(new[]{"--user",config.MinerUser});
   if(!String.IsNullOrEmpty(config.MinerPassword)){args.Add("--password");args.Add(config.MinerPassword);}
   args.AddRange(new[]{"--no-rocm","--devices-pci",ShortPci(pci),"--no-tui","--gpu-no-reset-oc","--api-host","127.0.0.1","--api-port",config.MinerPort.ToString()});
   return args.ToArray();
  }
 }
}
