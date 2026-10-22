using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace FlareIdle {
    // The window presents state; controller calls run outside the dispatcher.
    public sealed class Gui : Window {
        readonly bool preview;
        readonly DispatcherTimer timer;
        readonly TextBlock phase, detail, error, power, temperature, memory, tokens;
        readonly TextBlock modelName, inference, mining, shares, session;
        readonly TextBox endpoint;
        readonly Ellipse indicator;
        readonly Button startButton, miningButton, settingsButton;
        readonly Border errorBox;
        bool refreshing, commandPending, startupComplete, closingRequested, allowClose, stopSent, stoppingRequested;
        string uiError;
        Config config=new Config();
        Status current;

        static readonly Brush BackgroundBrush=ColorBrush("#171A1F"), PanelBrush=ColorBrush("#22262D");
        static readonly Brush ForegroundBrush=ColorBrush("#EEF0F3"), MutedBrush=ColorBrush("#9EA6B4");
        static readonly Brush GreenBrush=ColorBrush("#83D5AB"), AmberBrush=ColorBrush("#E9C083"), RedBrush=ColorBrush("#FF9FA5");
        static Brush ColorBrush(string value) { var b=new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));b.Freeze();return b; }
        static TextBlock Text(string value,double size,Brush brush) {return new TextBlock {Text=value,FontSize=size,Foreground=brush,TextWrapping=TextWrapping.Wrap};}
        static Border Panel(UIElement content,Thickness padding) {return new Border {Background=PanelBrush,CornerRadius=new CornerRadius(9),Padding=padding,Child=content};}
        static Button ActionButton(string text,bool primary) {
            var button=new Button {Content=text,Padding=new Thickness(15,9,15,9),Background=primary?ColorBrush("#DDE7F5"):ColorBrush("#303640"),
                Foreground=primary?ColorBrush("#18202B"):ForegroundBrush,BorderBrush=ColorBrush("#46505E"),BorderThickness=new Thickness(1),
                FontSize=13,MinHeight=36,Cursor=System.Windows.Input.Cursors.Hand};
            var template=new ControlTemplate(typeof(Button));var surface=new FrameworkElementFactory(typeof(Border));surface.Name="surface";
            surface.SetValue(Border.CornerRadiusProperty,new CornerRadius(4));
            surface.SetBinding(Border.BackgroundProperty,new Binding("Background"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});
            surface.SetBinding(Border.BorderBrushProperty,new Binding("BorderBrush"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});
            surface.SetBinding(Border.BorderThicknessProperty,new Binding("BorderThickness"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});
            surface.SetBinding(Border.PaddingProperty,new Binding("Padding"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});
            var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);surface.AppendChild(content);template.VisualTree=surface;
            var disabled=new Trigger {Property=UIElement.IsEnabledProperty,Value=false};disabled.Setters.Add(new Setter(UIElement.OpacityProperty,.45,"surface"));template.Triggers.Add(disabled);
            var pressed=new Trigger {Property=Button.IsPressedProperty,Value=true};pressed.Setters.Add(new Setter(UIElement.OpacityProperty,.75,"surface"));template.Triggers.Add(pressed);
            button.Template=template;return button;
        }
        static Border Metric(string label,out TextBlock value) {
            var body=new StackPanel();body.Children.Add(Text(label,11,MutedBrush));value=Text("-",23,ForegroundBrush);
            value.Margin=new Thickness(0,6,0,0);body.Children.Add(value);return Panel(body,new Thickness(15,12,15,12));
        }

        public Gui(bool preview=false) {
            this.preview=preview;
            Title="flare idle";Width=580;Height=675;MinWidth=520;MinHeight=610;
            WindowStartupLocation=WindowStartupLocation.CenterScreen;Background=BackgroundBrush;Foreground=ForegroundBrush;FontFamily=new FontFamily("Segoe UI");
            var scroll=new ScrollViewer {Background=BackgroundBrush,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
            var body=new StackPanel {Margin=new Thickness(23,20,23,20)};scroll.Content=body;Content=scroll;
            var heading=new DockPanel();settingsButton=ActionButton("settings",false);settingsButton.Margin=new Thickness(12,0,0,0);
            DockPanel.SetDock(settingsButton,Dock.Right);heading.Children.Add(settingsButton);
            var name=Text("flare idle",25,ForegroundBrush);name.FontWeight=FontWeights.SemiBold;heading.Children.Add(name);body.Children.Add(heading);
            settingsButton.Click+=delegate {OpenSettings();};
            var subtitle=Text("your model stays ready. mining uses the quiet moments.",12,MutedBrush);subtitle.Margin=new Thickness(0,6,0,18);body.Children.Add(subtitle);
            var stateRow=new StackPanel {Orientation=Orientation.Horizontal};
            indicator=new Ellipse {Width=9,Height=9,Fill=AmberBrush,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,9,0)};
            phase=Text("stopped",18,ForegroundBrush);phase.FontWeight=FontWeights.SemiBold;stateRow.Children.Add(indicator);stateRow.Children.Add(phase);body.Children.Add(stateRow);
            detail=Text("configure a model, then select start",12,MutedBrush);detail.Margin=new Thickness(0,5,0,14);body.Children.Add(detail);
            error=Text("",12,RedBrush);errorBox=Panel(error,new Thickness(11));errorBox.BorderBrush=ColorBrush("#71444B");errorBox.BorderThickness=new Thickness(1);
            errorBox.Margin=new Thickness(0,0,0,13);errorBox.Visibility=Visibility.Collapsed;body.Children.Add(errorBox);
            var metrics=new Grid {Margin=new Thickness(0,0,0,14)};metrics.ColumnDefinitions.Add(new ColumnDefinition());metrics.ColumnDefinitions.Add(new ColumnDefinition());
            metrics.RowDefinitions.Add(new RowDefinition());metrics.RowDefinitions.Add(new RowDefinition());
            var pp=Metric("gpu power / cap",out power);pp.Margin=new Thickness(0,0,5,5);
            var tp=Metric("gpu temperature",out temperature);tp.Margin=new Thickness(5,0,0,5);Grid.SetColumn(tp,1);
            var mp=Metric("vram used / total",out memory);mp.Margin=new Thickness(0,5,5,0);Grid.SetRow(mp,1);
            var op=Metric("model output rate",out tokens);op.Margin=new Thickness(5,5,0,0);Grid.SetRow(op,1);Grid.SetColumn(op,1);
            metrics.Children.Add(pp);metrics.Children.Add(tp);metrics.Children.Add(mp);metrics.Children.Add(op);body.Children.Add(metrics);
            var workers=new StackPanel();modelName=Text("flare / no model selected",13,ForegroundBrush);workers.Children.Add(modelName);
            inference=Text("model stopped",12,MutedBrush);inference.Margin=new Thickness(0,4,0,13);workers.Children.Add(inference);
            workers.Children.Add(new Border {Height=1,Background=ColorBrush("#363D48"),Margin=new Thickness(0,0,0,12)});
            mining=Text("mining disabled",13,ForegroundBrush);workers.Children.Add(mining);
            shares=Text("accepted -   rejected -",12,MutedBrush);shares.Margin=new Thickness(0,4,0,0);workers.Children.Add(shares);
            body.Children.Add(Panel(workers,new Thickness(15,13,15,13)));
            var label=Text("local api endpoint",11,MutedBrush);label.Margin=new Thickness(0,15,0,5);body.Children.Add(label);
            var route=new Grid();route.ColumnDefinitions.Add(new ColumnDefinition());route.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
            endpoint=new TextBox {Text="configure a model first",IsReadOnly=true,BorderThickness=new Thickness(0),Background=PanelBrush,Foreground=ForegroundBrush,
                FontFamily=new FontFamily("Consolas"),FontSize=12,Padding=new Thickness(9),VerticalContentAlignment=VerticalAlignment.Center};route.Children.Add(endpoint);
            var copy=ActionButton("copy",false);copy.Margin=new Thickness(6,0,0,0);Grid.SetColumn(copy,1);
            copy.Click+=delegate {try {if(current!=null&&!String.IsNullOrEmpty(current.Endpoint))Clipboard.SetText(current.Endpoint);}catch {}};route.Children.Add(copy);body.Children.Add(route);
            var actions=new Grid {Margin=new Thickness(0,18,0,0)};actions.ColumnDefinitions.Add(new ColumnDefinition());actions.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
            startButton=ActionButton("start",true);startButton.HorizontalAlignment=HorizontalAlignment.Left;
            miningButton=ActionButton("pause mining",false);Grid.SetColumn(miningButton,1);
            startButton.Click+=async delegate {if(current!=null&&current.WorkerAlive)await RequestStopAsync();else await StartAsync();};
            miningButton.Click+=async delegate {if(current!=null&&current.WorkerAlive&&!commandPending&&!closingRequested)await SendAsync(current.MiningAllowed?"pause-mining":"resume-mining");};
            actions.Children.Add(startButton);actions.Children.Add(miningButton);body.Children.Add(actions);
            session=Text("start is manual. minimize keeps running; close stops both workers.",11,MutedBrush);session.Margin=new Thickness(0,12,0,0);body.Children.Add(session);
            timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(500)};timer.Tick+=async delegate {await RefreshAsync();};
            Loaded+=OnLoaded;Closing+=OnClosing;Closed+=delegate {timer.Stop();};
        }

        async void OnLoaded(object sender,RoutedEventArgs args) {
            if(preview) {
                config.ModelAlias="local-model";config.MinerEnabled=true;config.MinerCoin="pearl";
                startupComplete=true;
                Render(new Status {Phase="mining",Detail="model resident; mining yields before each request",Endpoint="http://127.0.0.1:8000/v1",
                    MiningAllowed=true,ApiReady=true,PowerWatts=176,PowerLimitWatts=200,TemperatureC=61,MemoryUsedGiB=12.4,MemoryTotalGiB=24,
                    HashrateHps=42e9,TokensPerSecond=null,ActiveRequests=0,PendingRequests=0,AcceptedShares=4,RejectedShares=0,
                    WorkerAlive=true,FlarePid=100,MinerPid=101,IdleSeconds=12,LastHandoffMs=80,RequestCount=17});return;
            }
            try {config=Config.Load();}catch(Exception e){ShowUiError("settings could not be loaded: "+e.Message);}
            startupComplete=true;timer.Start();await RefreshAsync();
            if(!config.IsConfigured)OpenSettings();
        }

        void OpenSettings() {
            if(preview||commandPending||closingRequested||(current!=null&&current.WorkerAlive))return;
            var editor=new SettingsWindow(config) {Owner=this};
            if(editor.ShowDialog()==true&&editor.Saved) {
                try {config=Config.Load();uiError=null;if(current!=null)Render(current);}
                catch(Exception e){ShowUiError("settings could not be loaded: "+e.Message);}
            }
        }

        async Task StartAsync() {
            if(preview||commandPending||closingRequested)return;
            try {config=Config.Load();if(!config.IsConfigured){OpenSettings();return;}config.Validate();}
            catch(Exception e){ShowUiError("check settings: "+e.Message);return;}
            startupComplete=false;commandPending=true;stopSent=false;stoppingRequested=false;uiError=null;
            startButton.IsEnabled=false;settingsButton.IsEnabled=false;phase.Text="starting";indicator.Fill=AmberBrush;
            detail.Text="starting the controller and checking gpu ownership";
            try {await Task.Run(new Action(CoreClient.Start));}catch(Exception e){ShowUiError("could not start: "+e.Message);}
            finally {startupComplete=true;commandPending=false;}
            if(closingRequested&&!stopSent)await RequestStopAsync();await RefreshAsync();
        }

        async Task RefreshAsync() {
            if(preview||refreshing||allowClose)return;refreshing=true;
            try {
                var snapshot=await Task.Run(new Func<Status>(CoreClient.Snapshot));if(snapshot!=null)Render(snapshot);
                if(closingRequested&&startupComplete&&current!=null&&(current.SafeToClose||!current.WorkerAlive)){allowClose=true;Close();}
            }catch(Exception e){ShowUiError("status unavailable: "+e.Message);}finally{refreshing=false;}
        }

        async Task<bool> SendAsync(string action) {
            commandPending=true;startButton.IsEnabled=false;miningButton.IsEnabled=false;settingsButton.IsEnabled=false;bool ok=false;uiError=null;
            try {await Task.Run(new Action(delegate {CoreClient.Send(action);}));ok=true;}
            catch(Exception e){ShowUiError("controller request failed: "+e.Message);}finally{commandPending=false;}
            await RefreshAsync();return ok;
        }
        async Task RequestStopAsync() {
            if(stopSent||preview)return;stopSent=true;stoppingRequested=true;
            if(!await SendAsync("stop")){stopSent=false;stoppingRequested=false;startButton.IsEnabled=true;}
        }
        async void OnClosing(object sender,CancelEventArgs args) {
            if(allowClose||preview)return;
            if(startupComplete&&current!=null&&(current.SafeToClose||!current.WorkerAlive))return;
            args.Cancel=true;closingRequested=true;startButton.IsEnabled=false;miningButton.IsEnabled=false;settingsButton.IsEnabled=false;
            phase.Text="stopping";detail.Text="stopping workers before restoring the previous power cap";
            if(startupComplete)await RequestStopAsync();
        }

        void Render(Status s) {
            current=s;if(!s.WorkerAlive)stoppingRequested=false;
            bool failed=!String.IsNullOrEmpty(s.Error)||String.Equals(s.Phase,"error",StringComparison.OrdinalIgnoreCase);
            string state=String.IsNullOrEmpty(s.Phase)?"stopped":s.Phase.Replace('-',' ').Replace('_',' ').ToLowerInvariant();
            phase.Text=closingRequested||stoppingRequested?"stopping":state;indicator.Fill=failed?RedBrush:s.ApiReady?GreenBrush:AmberBrush;Title="flare idle - "+phase.Text;
            detail.Text=String.IsNullOrEmpty(s.Detail)?"configure a model, then select start":s.Detail;
            if(failed){error.Text=String.IsNullOrEmpty(s.Error)?"the controller needs attention":s.Error;errorBox.Visibility=Visibility.Visible;}
            else if(!String.IsNullOrEmpty(uiError)){error.Text=uiError;errorBox.Visibility=Visibility.Visible;}else{error.Text="";errorBox.Visibility=Visibility.Collapsed;}
            power.Text=Number(s.PowerWatts,"0")+" / "+Number(s.PowerLimitWatts,"0")+" w";
            temperature.Text=Number(s.TemperatureC,"0")+" \u00b0c";memory.Text=Number(s.MemoryUsedGiB,"0.0")+" / "+Number(s.MemoryTotalGiB,"0.0")+" gib";
            tokens.Text=s.TokensPerSecond.HasValue?Number(s.TokensPerSecond,"0.0")+" tok/s":"idle";
            string modelAlias=String.IsNullOrWhiteSpace(s.ModelAlias)?config.ModelAlias:s.ModelAlias;
            modelName.Text="flare / "+(String.IsNullOrWhiteSpace(modelAlias)?"no model selected":modelAlias);
            inference.Text=s.ApiReady?s.ActiveRequests+" active \u00b7 "+s.PendingRequests+" queued \u00b7 model stays resident":s.WorkerAlive?"loading model / api not ready":"model stopped";
            if(s.RequestCount>0)inference.Text+="\n"+s.RequestCount.ToString("N0",CultureInfo.InvariantCulture)+" requests \u00b7 last admission "+s.LastHandoffMs.ToString("0.0",CultureInfo.InvariantCulture)+" ms";
            string miningState=!config.MinerEnabled?"disabled":!s.WorkerAlive?"stopped":s.MinerPid>0?Hashrate(s.HashrateHps):
                !s.MiningAllowed?"paused by you":s.ActiveRequests>0||s.PendingRequests>0?"yielding to inference":"waiting for idle";
            string minerCoin=String.IsNullOrWhiteSpace(s.MinerCoin)?config.MinerCoin:s.MinerCoin;
            mining.Text=(String.IsNullOrWhiteSpace(minerCoin)?"mining":minerCoin)+" \u00b7 "+miningState;
            if(config.MinerEnabled&&s.WorkerAlive&&s.ActiveRequests==0&&s.PendingRequests==0&&s.IdleSeconds>0)mining.Text+=" \u00b7 idle "+s.IdleSeconds.ToString("0",CultureInfo.InvariantCulture)+"s";
            shares.Text="accepted "+s.AcceptedShares.ToString("N0",CultureInfo.InvariantCulture)+"   rejected "+s.RejectedShares.ToString("N0",CultureInfo.InvariantCulture);
            if(!String.IsNullOrEmpty(s.Endpoint))endpoint.Text=s.Endpoint;else endpoint.Text="http://127.0.0.1:"+config.Port+"/v1";
            startButton.Content=stoppingRequested?"stopping...":s.WorkerAlive?"stop":"start";
            startButton.IsEnabled=!preview&&!closingRequested&&!stoppingRequested&&!commandPending;
            miningButton.Content=s.MiningAllowed?"pause mining":"resume mining";
            miningButton.IsEnabled=!preview&&!closingRequested&&!stoppingRequested&&!commandPending&&s.WorkerAlive&&config.MinerEnabled&&!failed;
            settingsButton.IsEnabled=!preview&&!closingRequested&&!commandPending&&!s.WorkerAlive;
            session.Text=!s.WorkerAlive&&startupComplete&&!s.SafeToClose?"controller offline; power recovery may need attention":
                "start is manual. minimize keeps running; close stops both workers.";
        }
        void ShowUiError(string message){uiError=message;error.Text=message;errorBox.Visibility=Visibility.Visible;}
        static string Hashrate(double? value){
            if(!value.HasValue||Double.IsNaN(value.Value)||Double.IsInfinity(value.Value)||value.Value<0)return "rate unavailable";
            string[] units={"h/s","kh/s","mh/s","gh/s","th/s","ph/s","eh/s"};double rate=value.Value;int unit=0;
            while(rate>=1000&&unit<units.Length-1){rate/=1000;unit++;}
            return rate.ToString("0.0",CultureInfo.InvariantCulture)+" "+units[unit];
        }
        static string Number(double? value,string format){return value.HasValue&&!Double.IsNaN(value.Value)&&!Double.IsInfinity(value.Value)?value.Value.ToString(format,CultureInfo.InvariantCulture):"-";}
    }
}
