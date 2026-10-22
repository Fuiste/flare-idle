using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace FlareIdle {
    public sealed class SettingsWindow : Window {
        readonly Config original;
        readonly TextBox flare, model, publisher, trust, alias, power, reserve, concurrency, drafts, grace, port, cutoff, miner, coin, pools, user;
        readonly PasswordBox key,minerPassword;
        readonly ComboBox gpu;
        readonly CheckBox miningEnabled;
        readonly StackPanel miningFields;
        readonly TextBlock gpuHint, error;
        readonly Button save;
        public bool Saved {get;private set;}
        static readonly Brush Ink=new SolidColorBrush(Color.FromRgb(238,240,243)), Muted=new SolidColorBrush(Color.FromRgb(164,173,188));
        static readonly Brush Input=new SolidColorBrush(Color.FromRgb(238,241,246));
        sealed class DeviceChoice {
            public string Uuid;public GpuReading Reading;public string Label;
            public override string ToString(){return Label;}
        }

        public SettingsWindow(Config config) {
            original=config??new Config();Title="flare idle settings";Width=720;Height=820;MinWidth=610;MinHeight=600;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=new SolidColorBrush(Color.FromRgb(23,26,31));Foreground=Ink;FontFamily=new FontFamily("Segoe UI");
            var layout=new DockPanel {Background=Background};Content=new Border {Background=Background,Padding=new Thickness(23),Child=layout};
            var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);layout.Children.Add(footer);
            error=Label("",12);error.Foreground=new SolidColorBrush(Color.FromRgb(255,159,165));error.Margin=new Thickness(0,8,0,8);footer.Children.Add(error);
            var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
            var cancel=Button("cancel");cancel.IsCancel=true;cancel.Margin=new Thickness(0,0,8,0);buttons.Children.Add(cancel);
            save=Button("save settings");save.IsDefault=true;save.Click+=SaveSettings;buttons.Children.Add(save);footer.Children.Add(buttons);
            var scroll=new ScrollViewer {VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};layout.Children.Add(scroll);
            var body=new StackPanel {Margin=new Thickness(0,0,12,0)};scroll.Content=body;
            var title=Label("set up your model and miner",22);title.FontWeight=FontWeights.SemiBold;body.Children.Add(title);
            Hint(body,"save these settings, then select start in the main window. windows may ask for permission when you start; saving does not launch a model or miner.");
            Section(body,"flare model");
            Hint(body,"use a prepared flare package that fits your gpu and vram. current flare packages target supported blackwell gpus (sm120 / sm121); a plain model checkpoint is not a flare package.");
            flare=FileField(body,"flare executable",original.FlareExecutable,"executables (*.exe)|*.exe|all files (*.*)|*.*");
            model=FileField(body,"model package",original.ModelPackage,"flare model packages (*.safetensors)|*.safetensors|all files (*.*)|*.*");
            publisher=FileField(body,"publisher keys (optional)",original.PublisherKeysFile,"json files (*.json)|*.json|all files (*.*)|*.*");
            trust=FileField(body,"trust store (optional)",original.TrustStoreFile,"toml files (*.toml)|*.toml|all files (*.*)|*.*");
            Hint(body,"use the signing and trust files supplied or required by your package. model weights are never bundled with this app.");
            alias=Field(body,"api model name",original.ModelAlias);
            body.Children.Add(Label("api key",12));
            var keyRow=new Grid {Margin=new Thickness(0,4,0,10)};keyRow.ColumnDefinitions.Add(new ColumnDefinition());keyRow.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
            key=new PasswordBox {Password=original.ApiKey??"",Padding=new Thickness(8),FontSize=13,Background=Input,Foreground=Brushes.Black,BorderBrush=Brushes.Transparent};
            keyRow.Children.Add(key);var generate=Button("generate");generate.Margin=new Thickness(8,0,0,0);Grid.SetColumn(generate,1);
            generate.Click+=delegate {var bytes=new byte[24];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(bytes);key.Password=Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');};
            keyRow.Children.Add(generate);body.Children.Add(keyRow);Hint(body,"clients use this key for the local api. your settings are encrypted for this windows user.");
            port=Field(body,"local api port",original.Port.ToString(CultureInfo.InvariantCulture));
            Hint(body,"the api binds only to 127.0.0.1. no firewall or router changes are made.");
            Section(body,"gpu and scheduling");
            body.Children.Add(Label("nvidia gpu",12));gpu=new ComboBox {Margin=new Thickness(0,4,0,5),Padding=new Thickness(7),FontSize=13,Background=Input,Foreground=Brushes.Black,MinHeight=34};
            body.Children.Add(gpu);gpuHint=Label("checking available gpus...",11);gpuHint.Margin=new Thickness(0,0,0,10);gpuHint.Foreground=Muted;body.Children.Add(gpuHint);
            gpu.SelectionChanged+=delegate {ShowGpuHint();};
            power=Field(body,"power limit in watts (0 = leave unchanged)",original.PowerWatts.ToString(CultureInfo.InvariantCulture));
            Hint(body,"choose a cap within your gpu's supported range. if changed, the previous cap is restored after both workers stop. clocks, voltage and fan settings are left alone.");
            reserve=Field(body,"vram reserve in gib",original.ReserveGiB.ToString(CultureInfo.InvariantCulture));
            Hint(body,"leave room for the miner, desktop, and other applications. a model that does not fit should fail to load; reduce its size or concurrency.");
            concurrency=Field(body,"maximum concurrent requests",original.MaxConcurrency.ToString(CultureInfo.InvariantCulture));
            drafts=Field(body,"mtp draft tokens (0 = disabled)",original.MtpDraftTokens.ToString(CultureInfo.InvariantCulture));
            grace=Field(body,"idle delay before mining, seconds",original.IdleDelaySeconds.ToString(CultureInfo.InvariantCulture));
            Hint(body,"a short delay avoids restarting mining during brief agent tool calls. model weights stay loaded while mining.");
            cutoff=Field(body,"gpu core stop temperature, c",original.CoreCutoffC.ToString(CultureInfo.InvariantCulture));
            Section(body,"idle mining");
            miningEnabled=new CheckBox {Content="allow mining while the model is idle",IsChecked=original.MinerEnabled,Foreground=Ink,Margin=new Thickness(0,4,0,12)};
            body.Children.Add(miningEnabled);miningFields=new StackPanel();body.Children.Add(miningFields);
            miner=FileField(miningFields,"krig miner executable",original.MinerExecutable,"executables (*.exe)|*.exe|all files (*.*)|*.*");
            coin=Field(miningFields,"krig coin name",original.MinerCoin);
            miningFields.Children.Add(Label("pool urls (one per line)",12));
            pools=InputBox(String.Join(Environment.NewLine,original.MinerPools??new string[0]));pools.AcceptsReturn=true;pools.Height=72;pools.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;
            miningFields.Children.Add(pools);Hint(miningFields,"enter pool urls in the format supported by krig, for example stratum+ssl://pool.example:1234.");
            user=Field(miningFields,"wallet / worker or pool username",original.MinerUser);
            miningFields.Children.Add(Label("pool password (optional)",12));
            minerPassword=new PasswordBox {Password=original.MinerPassword??"",Padding=new Thickness(8),Margin=new Thickness(0,4,0,10),FontSize=13,Background=Input,Foreground=Brushes.Black,BorderBrush=Brushes.Transparent};
            miningFields.Children.Add(minerPassword);Hint(miningFields,"leave blank to use the miner's default password. use the value your pool requires.");
            Hint(miningFields,"use the format your pool expects. this app does not create a wallet, choose a pool, or take a mining fee.");
            miningEnabled.Checked+=delegate {miningFields.IsEnabled=true;miningFields.Opacity=1;};
            miningEnabled.Unchecked+=delegate {miningFields.IsEnabled=false;miningFields.Opacity=.55;};
            miningFields.IsEnabled=original.MinerEnabled;miningFields.Opacity=original.MinerEnabled?1:.55;
            Loaded+=async delegate {await LoadGpus();};
        }

        static TextBlock Label(string text,double size){return new TextBlock {Text=text,FontSize=size,Foreground=Ink,TextWrapping=TextWrapping.Wrap};}
        static Button Button(string text){return new Button {Content=text,Padding=new Thickness(13,8,13,8),FontSize=13,Background=Input,Foreground=Brushes.Black,MinHeight=34};}
        static TextBox InputBox(string text){return new TextBox {Text=text??"",Padding=new Thickness(8),FontSize=13,Margin=new Thickness(0,4,0,10),Background=Input,Foreground=Brushes.Black,BorderBrush=Brushes.Transparent};}
        static TextBox Field(Panel parent,string label,string value){parent.Children.Add(Label(label,12));var box=InputBox(value);parent.Children.Add(box);return box;}
        static void Hint(Panel parent,string value){var text=Label(value,11);text.Foreground=Muted;text.Margin=new Thickness(0,0,0,12);parent.Children.Add(text);}
        static void Section(Panel parent,string value){var text=Label(value,17);text.FontWeight=FontWeights.SemiBold;text.Margin=new Thickness(0,16,0,9);parent.Children.Add(text);}
        static TextBox FileField(Panel parent,string label,string value,string filter){
            parent.Children.Add(Label(label,12));var row=new Grid();row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
            var box=InputBox(value);row.Children.Add(box);var choose=Button("browse");choose.Margin=new Thickness(8,4,0,10);Grid.SetColumn(choose,1);
            choose.Click+=delegate {var dialog=new OpenFileDialog {Title=label,Filter=filter,CheckFileExists=true};if(dialog.ShowDialog()==true)box.Text=dialog.FileName;};
            row.Children.Add(choose);parent.Children.Add(row);return box;
        }
        async Task LoadGpus(){
            try {
                var readings=await Task.Run(new Func<GpuReading[]>(Gpu.Enumerate));gpu.Items.Clear();
                foreach(var reading in readings)gpu.Items.Add(new DeviceChoice {Uuid=reading.Uuid,Reading=reading,Label=reading.Name+" / "+Number(reading.MemoryTotalGiB)+" gib"});
                var selected=gpu.Items.Cast<DeviceChoice>().FirstOrDefault(x=>String.Equals(x.Uuid,original.GpuUuid,StringComparison.Ordinal));
                if(selected==null&&!String.IsNullOrEmpty(original.GpuUuid)){selected=new DeviceChoice {Uuid=original.GpuUuid,Label="saved gpu (currently unavailable)"};gpu.Items.Add(selected);}
                gpu.SelectedItem=selected??(gpu.Items.Count>0?gpu.Items[0]:null);ShowGpuHint();
            }catch(Exception e){gpuHint.Text="gpu discovery unavailable: "+e.Message;}
        }
        void ShowGpuHint(){
            var choice=gpu.SelectedItem as DeviceChoice;
            if(choice==null){gpuHint.Text="no nvidia gpu detected. check the driver and reopen settings.";return;}
            if(choice.Reading==null){gpuHint.Text="the saved gpu must be available before starting.";return;}
            var r=choice.Reading;gpuHint.Text="current cap "+Number(r.PowerLimitWatts)+" w; supported "+Number(r.MinPowerLimitWatts)+"-"+Number(r.MaxPowerLimitWatts)+" w. unsupported readings remain unavailable.";
            if(r.ComputeMajor.HasValue&&r.ComputeMinor.HasValue&&(r.ComputeMajor.Value!=12||(r.ComputeMinor.Value!=0&&r.ComputeMinor.Value!=1)))
                gpuHint.Text+=" current flare targets sm120 / sm121; this gpu may not be compatible.";
        }
        static string Number(double? value){return value.HasValue?value.Value.ToString("0.#",CultureInfo.InvariantCulture):"unknown";}
        static int Integer(TextBox box,string label){int value;if(!Int32.TryParse(box.Text.Trim(),NumberStyles.Integer,CultureInfo.InvariantCulture,out value))throw new ArgumentException(label+" must be a whole number");return value;}
        void SaveSettings(object sender,RoutedEventArgs args){
            try {
                var next=original.Clone();
                next.FlareExecutable=flare.Text.Trim();next.ModelPackage=model.Text.Trim();next.PublisherKeysFile=publisher.Text.Trim();next.TrustStoreFile=trust.Text.Trim();
                next.ModelAlias=alias.Text.Trim();next.ApiKey=key.Password;next.Port=Integer(port,"api port");
                var device=gpu.SelectedItem as DeviceChoice;next.GpuUuid=device==null?"":device.Uuid;
                next.PowerWatts=Integer(power,"power limit");next.ReserveGiB=Integer(reserve,"vram reserve");next.MaxConcurrency=Integer(concurrency,"concurrent requests");
                next.MtpDraftTokens=Integer(drafts,"mtp draft tokens");next.IdleDelaySeconds=Integer(grace,"idle delay");next.CoreCutoffC=Integer(cutoff,"stop temperature");
                next.MinerEnabled=miningEnabled.IsChecked==true;next.MinerExecutable=miner.Text.Trim();next.MinerCoin=coin.Text.Trim();next.MinerUser=user.Text.Trim();next.MinerPassword=minerPassword.Password;
                next.MinerPools=pools.Text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(x=>x.Trim()).Where(x=>x.Length>0).ToArray();
                next.Validate();
                if(next.PowerWatts>0&&device!=null&&device.Reading!=null) {
                    var r=device.Reading;
                    if(r.MinPowerLimitWatts.HasValue&&next.PowerWatts<r.MinPowerLimitWatts.Value||r.MaxPowerLimitWatts.HasValue&&next.PowerWatts>r.MaxPowerLimitWatts.Value)
                        throw new ArgumentException("power limit is outside this gpu's supported range");
                }
                next.Save();Saved=true;DialogResult=true;
            }catch(Exception e){error.Text=e.Message;}
        }
    }
}
