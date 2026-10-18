using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MabiSync.Client;
using MabiSync.Core;

internal static partial class Program
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _failed;
    private static Type _windowType = null!;
    private static string _folder = null!;
    private static string _appPath=null!;
    [STAThread]
    private static int Main(string[] args)
    {
        if(args is ["--window-fixture"])return WindowFixture();
        if (args.Length < 1) throw new ArgumentException("Supply the application assembly path");
        _appPath=Path.GetFullPath(args[0]);
        _folder = Path.Combine(Path.GetTempPath(), "mabisync-wpf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        var app = (Application)Activator.CreateInstance(assembly.GetType("MabiSync.App.App")!)!;
        app.GetType().GetMethod("InitializeComponent")!.Invoke(app, null);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _windowType = assembly.GetType("MabiSync.App.MainWindow")!;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            try { await InformationOverlay();await HeaderActions();await UpdateUi();await CompactReadiness();await DefaultsPersistence();await WindowDiscovery();await TimingUi(); await SnapshotRace(); await RoomUi(); await GrowingRosterAndJoinerBindings(); await LongScoreDisplay(); await PhaseViews(); await ClosingCancelsBeforeCleanup(); await DisposedService(); FaultLogs(); if (args.Length > 1) { await OverlayCommands(args[1]);await LanUi(args[1]);await CommunityUi(args[1]);await RecoveryUi(args[1]);await OfflineCloseUi();await LiveRoom(args[1]); await EarlyEnsembleUi(args[1]); } }
            catch (Exception error) { _failed++; Console.WriteLine("FAIL " + Unwrap(error)); }
            finally { app.Shutdown(); Dispatcher.ExitAllFrames(); }
        });
        Dispatcher.Run();
        Console.WriteLine($"WPF checks failed: {_failed}; desktop inputs: 0; user configuration: untouched");
        return _failed == 0 ? 0 : 1;
    }
    private static Window NewWindow()
    {
        var window=(Window)Activator.CreateInstance(_windowType, Private, null, [Path.Combine(_folder, Guid.NewGuid()+".json"), false], null)!;
        var nickname=(TextBox)window.FindName("NicknameBox");
        Check(nickname.Text=="","Fresh profile contains a prefilled nickname");
        Check(((ComboBox)window.FindName("ModeBox")).SelectedIndex==1,"Fresh profile does not default to ensemble");
        Check(((ComboBox)window.FindName("StartInputKind")).SelectedIndex==1,"Fresh start binding does not default to game button");
        Check(((ComboBox)window.FindName("StopInputKind")).SelectedIndex==0 && ((TextBox)window.FindName("StopKeyBox")).Text=="Space","Fresh stop binding is not Space");
        nickname.Text="Fixture";
        var input=(CheckBox)window.FindName("EnableGameInput");
        Check(input.IsChecked==true && !Field<InputRouter>(window,"_inputRouter").UseSimulation,"Game input is not the default active option");
        // Every integration probe explicitly opts into simulation before joining a room.
        input.IsChecked=false;return window;
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static async Task HeaderActions()
    {
        var window=NewWindow();
        try {
            Check(!window.Topmost,"Main window must default to unpinned");
            var pin=window.FindName("PinWindowButton") as Button;var privacy=window.FindName("CapturePrivacyButton") as Button;
            Check(pin is not null && privacy is not null && window.FindName("SponsorButton") is Button,"Header icon actions missing");
            var versionText=window.FindName("VersionText") as TextBlock;
            var expectedVersion=_windowType.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
            Check(versionText is not null && versionText.Text==$"v{expectedVersion}" && window.Title.Contains(versionText.Text),"UI version must match the running application, including preview suffix");
            Check(window.FindName("CheckUpdateButton") is Button { Content: "检查更新", IsEnabled: true }, "Manual GitHub update check is missing");
            Check(pin!.ToolTip!.ToString()!.Contains("已关闭") && privacy!.ToolTip!.ToString()!.Contains("已关闭"),"Window pin or recording exclusion default is wrong");
            Check(privacy!.ToolTip!.ToString()!.Contains("NVIDIA"),"Capture toggle omits the recorder compatibility hint");
            pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(window.Topmost,"Pin button did not enable topmost");
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            var policy=Field<object>(window,"_windowCapturePolicy");
            var policyType=policy.GetType();
            var affinity=policyType.GetMethod("ReadAffinity")!;
            Check((uint)affinity.Invoke(policy,[window])! == 0,"Fresh window is unexpectedly excluded from recording");
            privacy!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(OperatingSystem.IsWindowsVersionAtLeast(10,0,19041))Check((uint)affinity.Invoke(policy,[window])! == 0x11,"Opt-in capture privacy was not applied");
            privacy!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check((uint)affinity.Invoke(policy,[window])! == 0,"Capture privacy did not turn off");
            Check(privacy.ToolTip!.ToString()!.Contains("已关闭"),"Disabled privacy hint is stale");
            ((Button)window.FindName("SponsorButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var sponsor=Field<Window>(window,"_sponsorWindow");
            Check(sponsor.Owner==window && sponsor.Title.Contains("支持"),"Sponsor entry did not open owned secondary window");
            Check(Children<Button>((DependencyObject)sponsor.Content).Any(button=>button.Content?.ToString()=="爱发电") && Children<Button>((DependencyObject)sponsor.Content).Any(button=>button.Content?.ToString()=="Patreon"),"Sponsor providers missing");
            ((Button)window.FindName("SponsorButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(ReferenceEquals(sponsor,Field<Window>(window,"_sponsorWindow")),"Sponsor clicks create duplicate windows");
            Check(window.Icon is not null && sponsor.Icon is not null,"Application windows lack product icon");
            Check((uint)affinity.Invoke(policy,[sponsor])! == 0,"Secondary window unexpectedly blocks capture while hiding is off");
            privacy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(OperatingSystem.IsWindowsVersionAtLeast(10,0,19041))Check((uint)affinity.Invoke(policy,[sponsor])! == 0x11,"Privacy enable did not reach existing secondary windows");
            privacy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check((uint)affinity.Invoke(policy,[sponsor])! == 0,"Privacy disable did not clear capture protection from existing secondary windows");
            pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(!sponsor.Topmost,"Unpin did not reach secondary window");
            pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(sponsor.Topmost,"Pin did not reach secondary window");
            var images=Environment.GetEnvironmentVariable("MABISYNC_UI_OUTPUT");if(images is not null){Directory.CreateDirectory(images);Render(sponsor,400,320,Path.Combine(images,"sponsor-dialog.png"));Render(window,1180,900,Path.Combine(images,"header-actions.png"));}
            sponsor.Close();
            Console.WriteLine("PASS WPF header toggles, native capture exclusion and sponsor dialog; external links opened: 0");
        }finally{await Close(window);}
    }
    private static bool InsideStatusDetails(DependencyObject item)
    {
        for(var parent=LogicalTreeHelper.GetParent(item);parent is not null;parent=LogicalTreeHelper.GetParent(parent))
            if(parent is Expander expander && expander.Name=="StatusSection")return true;
        return false;
    }
    private static async Task CompactReadiness()
    {
        var window=NewWindow();var client=Field<RoomClient>(window,"_client");
        try {
            typeof(RoomClient).GetField("_session",Private)!.SetValue(client,new SessionIdentity("FIXTUR","host","fixture","epoch","ws://127.0.0.1:3132/"));
            typeof(RoomClient).GetField("_connected",Private)!.SetValue(client,true);
            SetSnapshot(client,["host","member"]);Call(window,"Refresh");Layout(window,780,700);
            Check(((TextBlock)window.FindName("PhaseText")).Text=="等待全员就绪","Host idle phase incorrectly asks the host to prepare");
            var section=(Expander)window.FindName("StatusSection");section.IsExpanded=false;Layout(window,780,700);
            var start=(Button)window.FindName("StartButton");
            Check(!InsideStatusDetails(start),"Start is hidden inside collapsible status content");
            Check(start.IsEnabled && start.ActualHeight>0,"Collapsed status makes start unusable");
            var room=client.Room!.Value;var nodes=System.Text.Json.Nodes.JsonNode.Parse(room.GetRawText())!;
            nodes["members"]![1]!["ready"]=true;nodes["members"]![0]!["instrument"]="钢琴";
            var snapshotType=typeof(RoomClient).GetNestedType("Snapshot",BindingFlags.NonPublic)!;
            void Publish()=>typeof(RoomClient).GetField("_snapshot",Private)!.SetValue(client,Activator.CreateInstance(snapshotType,[JsonSerializer.SerializeToElement(nodes)]));
            Publish();Call(window,"Refresh");
            Check(((TextBlock)window.FindName("PhaseText")).Text=="全员已就绪","Host does not reflect all selected members ready");
            Check(((TextBlock)window.FindName("RoleSummaryText")).Text.Contains("钢琴"),"Identity summary omits instrument");
            typeof(RoomClient).GetField("_session",Private)!.SetValue(client,new SessionIdentity("FIXTUR","member","fixture","epoch","ws://127.0.0.1:3132/"));Call(window,"Refresh");Layout(window,780,700);
            var ready=(Button)window.FindName("ReadyButton");
            Check(((TextBlock)window.FindName("PhaseText")).Text=="已就绪","Member ready phase missing");
            Check(ready.Background.ToString()=="#FF173D32","Ready button is not green");
            Check(!InsideStatusDetails(ready) && ready.IsEnabled && ready.ActualHeight>0,"Collapsed status hides member readiness");
            nodes["members"]![1]!["ready"]=false;Publish();Call(window,"Refresh");
            Check(((TextBlock)window.FindName("PhaseText")).Text=="未就绪" && ready.Background.ToString()=="#FF482730","Member unready phase/button is not red");
            nodes["performance"]=System.Text.Json.Nodes.JsonNode.Parse("{\"id\":\"ended-fixture\",\"phase\":\"stopped\",\"mode\":\"emote\",\"startAction\":{\"id\":\"ended-start\",\"targetServerMs\":0}}");Publish();Call(window,"Refresh");
            Check(((TextBlock)window.FindName("PhaseText")).Text=="未就绪","Previous round masks member readiness");
            nodes["performance"]=null;Publish();Call(window,"Refresh");
            var images=Environment.GetEnvironmentVariable("MABISYNC_UI_OUTPUT");
            if(images is not null){Directory.CreateDirectory(images);Render(window,780,700,Path.Combine(images,"member-collapsed.png"));section.IsExpanded=true;Render(window,1180,900,Path.Combine(images,"member-expanded.png"));}
            Console.WriteLine("PASS WPF compact role-specific readiness, instrument summary and persistent actions");
        }finally{await Close(window);}
    }
    private static async Task UpdateUi()
    {
        var window=NewWindow();
        try {
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            _windowType.GetMethod("ShowUpdate",Private)!.Invoke(window,[new ReleaseUpdate("0.1.0","9.0.0",true,new Uri(GitHubUpdates.Repository+"/releases/tag/v9.0.0"),[])]);
            await Task.Delay(30);
            var dialog=Field<Window>(window,"_updateWindow");
            var panel=(StackPanel)dialog.Content;
            Check(panel.Children.OfType<Button>().Any(b=>Equals(b.Content,"下载更新") && b.IsEnabled),"Update dialog omits explicit download consent");
            Check(!Field<bool>(window,"_updating"),"Showing an available release began downloading automatically");
            var images=Environment.GetEnvironmentVariable("MABISYNC_UI_OUTPUT");
            if(images is not null){Directory.CreateDirectory(images);Render(dialog,480,400,Path.Combine(images,"update-dialog.png"));Render(window,780,700,Path.Combine(images,"version-header-narrow.png"));}
            dialog.Close();
        }finally{window.Close();}
        Console.WriteLine("PASS WPF update dialog waits for explicit download and preserves the version header");
    }
    private static async Task DefaultsPersistence()
    {
        var window=NewWindow();var path=Field<string>(window,"_settings");
        try {
            Check(((FrameworkElement)window.FindName("StartButtonHelp")).ToolTip?.ToString()=="请选中乐谱的使用按钮位置。","Start button help is missing");
            ((ComboBox)window.FindName("ModeBox")).SelectedIndex=0;
            ((ComboBox)window.FindName("ModeBox")).SelectedIndex=1;
            Check(((ComboBox)window.FindName("StartInputKind")).SelectedIndex==1,"Mode switching lost the uncalibrated button preference");
            ((ComboBox)window.FindName("StartInputKind")).SelectedIndex=0;
            ((Button)window.FindName("CapturePrivacyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Call(window,"SaveSettings");
        } finally {await Close(window);}
        var restored=(Window)Activator.CreateInstance(_windowType,Private,null,[path,false],null)!;
        try {
            Check(((ComboBox)restored.FindName("StartInputKind")).SelectedIndex==0,"Saved unbound shortcut preference was replaced by the new default");
            Check(((TextBox)restored.FindName("StopKeyBox")).Text=="Space","Saved stop binding was lost");
            Check(Field<bool>(restored,"_hideFromCapture"),"Explicit capture hiding preference was not restored");
            ((Button)restored.FindName("CapturePrivacyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var saved=JsonDocument.Parse(File.ReadAllText(path));
            Check(!saved.RootElement.GetProperty("hideFromCapture").GetBoolean(),"Disabling capture hiding did not save immediately");
        }finally{await Close(restored);}
        Console.WriteLine("PASS WPF fresh ensemble/button/Space defaults and binding preference persistence");
    }
    private static int WindowFixture()
    {
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var fixture=new Window{Title="MabiSync minimized fixture",Width=500,Height=300,ShowActivated=false,ShowInTaskbar=false,WindowState=WindowState.Minimized};
        fixture.Loaded+=async(_,_)=>{
            using var self=System.Diagnostics.Process.GetCurrentProcess();
            Console.WriteLine(JsonSerializer.Serialize(new{Handle=(long)new System.Windows.Interop.WindowInteropHelper(fixture).Handle,ProcessId=self.Id,Started=self.StartTime.ToUniversalTime().Ticks,Path=self.MainModule!.FileName}));Console.Out.Flush();
            await Task.Run(()=>Console.In.ReadLine());fixture.Close();app.Shutdown();
        };
        app.Run(fixture);return 0;
    }
    private static async Task WindowDiscovery()
    {
        var launch=new System.Diagnostics.ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        launch.ArgumentList.Add(typeof(Program).Assembly.Location);launch.ArgumentList.Add("--window-fixture");
        using var child=System.Diagnostics.Process.Start(launch)!;var window=NewWindow();Window? capture=null;
        try {
            var line=await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var wire=JsonDocument.Parse(line!);var value=wire.RootElement;
            var target=new GameWindowId((nint)value.GetProperty("Handle").GetInt64(),value.GetProperty("ProcessId").GetInt32(),value.GetProperty("Started").GetInt64(),value.GetProperty("Path").GetString()!);
            var platform=Field<object>(window,"_gamePlatform");var platformType=platform.GetType();
            var games=(IReadOnlyList<GameWindowState>)platformType.GetMethod("ListGameWindows")!.Invoke(platform,[true])!;
            var state=games.SingleOrDefault(s=>s.Identity.Handle==target.Handle);
            Check(state is{Minimized:true,Title:"MabiSync minimized fixture"},"Minimized titled window missing from explicit other-window list");
            Check((bool)platformType.GetMethod("IsProcessRunning")!.Invoke(null,[target with{Handle=0}])!,"Capture process validity depends on window geometry or handle");
            Check(!(bool)platformType.GetMethod("IsProcessRunning")!.Invoke(null,[target with{ProcessStartedUtcTicks=target.ProcessStartedUtcTicks+1}])!,"Capture accepted a different process generation");
            ((CheckBox)window.FindName("ShowOtherWindows")).IsChecked=true;
            var box=(ComboBox)window.FindName("GameWindowBox");box.SelectedItem=box.Items.Cast<object>().Single(item=>(GameWindowState)item.GetType().GetProperty("State")!.GetValue(item)! == state);
            box.ItemsSource=null;
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Check(box.Items.Count>0 && (bool)Field<object>(window,"_startupWindowsRefreshed"),"Startup did not refresh available windows");
            box.SelectedItem=box.Items.Cast<object>().Single(item=>(GameWindowState)item.GetType().GetProperty("State")!.GetValue(item)! == state);
            Call(window,"RefreshWindowsClick",box,new RoutedEventArgs());
            Check(box.SelectedItem?.GetType().GetProperty("Label")!.GetValue(box.SelectedItem)?.ToString()?.Contains("MabiSync minimized fixture")==true,"Refresh lost minimized selection or its title");
            ((CheckBox)window.FindName("EnableGameInput")).IsChecked=true;
            ((ComboBox)window.FindName("ModeBox")).SelectedIndex=0;
            Call(window,"ValidateLocalReadiness");
            Check(!((TextBlock)window.FindName("InputStateText")).Text.StartsWith("F9："),"UI advertises F9 when no hotkey is registered");
            var client=Field<RoomClient>(window,"_client");
            typeof(RoomClient).GetField("_session",Private)!.SetValue(client,new SessionIdentity("FIXTUR","member","fixture","epoch","ws://127.0.0.1:3132/"));
            SetSnapshot(client,["host","member"]);((ComboBox)window.FindName("ModeBox")).SelectedIndex=1;Call(window,"Refresh");
            ((ComboBox)window.FindName("StartInputKind")).SelectedIndex=0;((TextBox)window.FindName("StartKeyBox")).Text="Space";Call(window,"InputChanged");
            Call(window,"ValidateLocalReadiness");
            Check(Field<GameInputProfile>(window,"_gameProfile").Ensemble is {Start:null,StopIsToggle:false},"A manual joiner's unused start made its stop a toggle");
            typeof(RoomClient).GetField("_session",Private)!.SetValue(client,new SessionIdentity("FIXTUR","host","fixture","epoch","ws://127.0.0.1:3132/"));Call(window,"Refresh");
            Check(Field<GameInputProfile>(window,"_gameProfile").Ensemble.Start is KeyBinding {Key:0x20},"Promoting to initiator did not restore the saved start binding");
            try{Call(window,"ValidateLocalReadiness");throw new Exception("Initiator accepted the same start/stop key");}
            catch(TargetInvocationException error) when(error.InnerException is InvalidOperationException issue && issue.Message.Contains("不同")){}
            typeof(RoomClient).GetField("_session",Private)!.SetValue(client,null);typeof(RoomClient).GetField("_snapshot",Private)!.SetValue(client,null);Call(window,"Refresh");
            ((CheckBox)window.FindName("EnableGameInput")).IsChecked=false;
            Console.WriteLine("PASS WPF missing global hotkey does not block real input readiness; desktop inputs: 0");
            var observer=Field<object>(window,"_gameObservation");var observationType=observer.GetType();
            observationType.GetField("_game",Private)!.SetValue(observer,target);
            var deviceType=platformType.Assembly.GetType("MabiSync.Windows.CaptureDevice")!;
            observationType.GetField("_device",Private)!.SetValue(observer,Activator.CreateInstance(deviceType,["fixture","Fixture capture channel",new[]{"127.0.0.1"}]));
            var observerState=observationType.GetProperty("Current")!.GetValue(observer)!;
            var fakeCapture=Activator.CreateInstance(observerState.GetType(),[observerState.GetType().GetProperty("Game")!.GetValue(observerState),0L,true,"Fixture running"]);
            observationType.GetField("_current",Private)!.SetValue(observer,fakeCapture);
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            Call(window,"CaptureChannelsClick",box,new RoutedEventArgs());
            capture=Field<Window>(window,"_captureChannels");
            Call(capture,"RefreshChannels");
            var captureSelection=capture.GetType().GetEvent("GameSelected");
            Check(captureSelection is not null,"Capture window has no shared target selection event");
            box.SelectedItem=null;
            Call(capture,"RefreshChannels");
            Check(Field<GameInputProfile?>(window,"_gameProfile")?.Window==target,"Capture selection did not update the local input window");
            Check(((ComboBox)capture.FindName("GamesBox")).SelectedItem is not null && ((ComboBox)capture.FindName("DevicesBox")).SelectedItem is not null,"Reopened capture lost its running game/channel");
            Check(observationType.GetProperty("Current")!.GetValue(observer)==fakeCapture,"Opening capture reset active observation");
            var local=(FrameworkElement)window.FindName("LocalActionCard");var roster=(FrameworkElement)window.FindName("RosterCard");var workflow=(Panel)local.Parent;
            Check(local.Visibility==Visibility.Visible && workflow.Children.IndexOf(local)<workflow.Children.IndexOf(roster),"Preparation controls are hidden or behind the roster");
            Console.WriteLine("PASS native minimized window titles, explicit other-window selection and persistent capture binding; game inputs: 0");
        }finally {
            capture?.Close();await child.StandardInput.WriteLineAsync("exit");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));await Close(window);
        }
    }
    private static async Task TimingUi()
    {
        var window=NewWindow();
        try {
            var box=window.FindName("TimingOffsetBox") as TextBox;
            var provider=window.FindName("PublicProviderBox") as ComboBox;
            Check(provider is not null && provider.Items.Count==3,"Connection choices should be server, LAN and Cloudflare");
            Check(provider!.SelectedIndex==2,"Fresh profile does not default to encrypted server");
            Check(window.FindName("EasyTierNodesBox") is null,"Removed provider still exposes node settings");
            provider.SelectedIndex=1;
            Check(((FrameworkElement)window.FindName("LanControls")).Visibility==Visibility.Visible,"LAN controls hidden in LAN mode");
            Check(window.FindName("LanTutorialButton") is Button,"LAN mode lacks a connection guide");
            provider.SelectedIndex=0;
            Check(((FrameworkElement)window.FindName("LanControls")).Visibility==Visibility.Collapsed,"LAN controls leaked into public mode");
            Check(box is not null,"Timing adjustment has no numeric editor");
            box!.Text="-37";
            Check(Field<ActionScheduler>(window,"_scheduler").TimingOffsetMs==-37,"Numeric advance did not reach scheduler");
            var slider=(Slider)window.FindName("TimingOffsetSlider");slider.Value=18;
            Check(box.Text=="18" && slider.SmallChange==1,"Slider and numeric editor do not share millisecond precision");
            box.Text="-";Check(Field<ActionScheduler>(window,"_scheduler").TimingOffsetMs==18,"Incomplete draft changed timing");
            box.Text="+12";Check(Field<ActionScheduler>(window,"_scheduler").TimingOffsetMs==12,"Signed delay rejected");
            var timingSection=window.FindName("TimingAdjustmentSection") as Expander;
            Check(timingSection?.IsExpanded==true,"Timing adjustment is not expanded by default");
            timingSection!.IsExpanded=false;
            Check(timingSection.Header?.ToString()?.Contains("+12")==true && timingSection.Header.ToString()!.Contains("延后"),"Collapsed timing adjustment hides its result");
            foreach(var name in new[]{"RoomSection","ModeSection","LocalActionSection","RosterSection","StatusSection"}) {
                var section=window.FindName(name) as Expander;Check(section is not null,"Card cannot collapse: "+name);
                section!.IsExpanded=false;Layout(window,780,700);section.IsExpanded=true;
            }
            Check(box.Text=="12","Collapsing cards changed the saved timing adjustment");
            Check(((ComboBox)window.FindName("StopChoice")).SelectedIndex==2,"Fresh settings do not default to first cycle");
            var stop=(DependencyObject)window.FindName("StopControls");var parent=stop;var inStatus=false;
            while(parent is not null){if(parent==window.FindName("StatusCard")){inStatus=true;break;}parent=LogicalTreeHelper.GetParent(parent);}
            Check(inStatus,"Stop options are outside the bottom status card");
            Layout(window,1180,900);
            foreach(var combo in Children<ComboBox>((DependencyObject)window.Content)){
                var selected=combo.SelectedIndex;
                var wheel=new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,-120){RoutedEvent=System.Windows.Input.Mouse.PreviewMouseWheelEvent};
                combo.RaiseEvent(wheel);
                Check(wheel.Handled && combo.SelectedIndex==selected,"Wheel can modify a dropdown");
            }
            var background=((System.Windows.Media.SolidColorBrush)window.Background).Color;
            Check(background.R<50 && background.G<50 && background.B<60,"Window is not dark");
            var horizontal=new System.Windows.Controls.Primitives.ScrollBar{Orientation=Orientation.Horizontal,Minimum=0,Maximum=200,ViewportSize=20,Width=400};
            horizontal.Style=(Style)window.FindResource(typeof(System.Windows.Controls.Primitives.ScrollBar));horizontal.ApplyTemplate();
            horizontal.Measure(new Size(400,30));horizontal.Arrange(new Rect(0,0,400,10));horizontal.UpdateLayout();
            var track=(System.Windows.Controls.Primitives.Track)horizontal.Template.FindName("PART_Track",horizontal);
            Check(track.Orientation==Orientation.Horizontal && !track.IsDirectionReversed && horizontal.ActualHeight==10,"Dark horizontal scrollbar has vertical geometry");
            Console.WriteLine("PASS WPF numeric timing, one millisecond slider, safe dropdown wheel and dark defaults");
        }finally{await Close(window);}
    }
    private static void Call(object target, string name,params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static Exception Unwrap(Exception error) => error is TargetInvocationException { InnerException: { } inner } ? Unwrap(inner) : error;
    private static void SetSnapshot(RoomClient client, string[] ids, object? performance = null)
    {
        var room = JsonSerializer.SerializeToElement(new { code="FIXTUR", hostId="host", leaderId="host", performance, observedCharacters=Array.Empty<object>(),
            members=ids.Select((id,index)=>new {id,nickname=id,role=id=="host"?"host":"member",connected=true,ready=false,
                ensembleGroup=1,ensembleOrder=index+1,ensembleRole=id=="host"?"initiator":"joiner",gameIdentity=(object?)null,music=(object?)null,
                ensembleFeedback=new {state="unconfirmed",text="等待核对"}}).ToArray() });
        var type=typeof(RoomClient).GetNestedType("Snapshot",BindingFlags.NonPublic)!;
        typeof(RoomClient).GetField("_snapshot",Private)!.SetValue(client,Activator.CreateInstance(type,[room]));
    }
    private static async Task SnapshotRace()
    {
        var window=NewWindow(); var client=Field<RoomClient>(window,"_client");
        try
        {
            typeof(RoomClient).GetField("_session",Private)!.SetValue(client,new SessionIdentity("FIXTUR","host","fixture","epoch","ws://127.0.0.1:3132/"));
            SetSnapshot(client,["host"]);
            var rows=(INotifyCollectionChanged)Field<object>(window,"_members"); var swapped=false;
            rows.CollectionChanged+=(_,e)=> { if (!swapped && e.Action==NotifyCollectionChangedAction.Add) {swapped=true;SetSnapshot(client,["host","new-member"]);} };
            Call(window,"Refresh");
            Check(swapped,"The mid-render state transition was not exercised");
            Call(window,"Refresh");
            Check(((DataGrid)window.FindName("MembersGrid")).Items.Count==2,"Next frame lost the joining member");
            Console.WriteLine("PASS WPF member arrival during room rendering");
        }
        finally { await Close(window); }
    }
    private static async Task RoomUi()
    {
        var window=NewWindow();var client=Field<RoomClient>(window,"_client");
        try {
            typeof(RoomClient).GetField("_session",Private)!.SetValue(client,new SessionIdentity("FIXTUR","host","fixture","epoch","ws://127.0.0.1:3132/"));
            SetSnapshot(client,["host","member"]);((ComboBox)window.FindName("ModeBox")).SelectedIndex=1;
            typeof(RoomClient).GetField("_connected",Private)!.SetValue(client,true);Call(window,"Refresh");Layout(window,1180,900);
            var grid=(DataGrid)window.FindName("MembersGrid");var host=grid.Items[0];
            Check((string)host.GetType().GetProperty("Status")!.GetValue(host)! != "未就绪","Host incorrectly appears unready");
            Check(((Button)window.FindName("ReadyButton")).Visibility==Visibility.Collapsed,"Host exposes redundant readiness");
            Check(window.FindName("CountdownProgress") is ProgressBar,"Countdown has no progress presentation");
            var score=(DependencyObject)window.FindName("ScoreObservationText");var status=(DependencyObject)window.FindName("StatusCard");
            Check(status is not null && IsDescendant(score,status),"Score information is outside the status card");
            Check(grid.Columns.Any(c=>c.Header?.ToString()=="乐器"),"Instrument column missing");
            Check(grid.Columns.IndexOf(grid.Columns.First(c=>c.Header?.ToString()=="乐器"))==grid.Columns.IndexOf(grid.Columns.First(c=>c.Header?.ToString()=="顺序"))+1,"Instrument is not after order");
            Check((bool)host.GetType().GetProperty("CanEdit")!.GetValue(host)!,"Host roster editing is disabled");
            Check((bool)host.GetType().GetProperty("IsSelf")!.GetValue(host)!,"Local character lacks self emphasis");
            var groupEditor=Children<TextBox>(grid).First(box=>box.Tag as string=="group");
            Check(!Equals(((Border)window.FindName("StatusCard")).Background,window.FindResource("FieldBrush")),"Status surface has no distinct treatment");
            groupEditor.SelectAll();groupEditor.SelectedText="12";Call(window,"Refresh");
            Check(groupEditor.Text=="12","Unchanged room refresh overwrote a typed group draft");groupEditor.GetBindingExpression(TextBox.TextProperty)!.UpdateTarget();
            var roleEditor=Children<ComboBox>(grid).First(box=>box.SelectedValuePath=="Value");
            Check(roleEditor.SelectedValue as string=="initiator" && roleEditor.IsEnabled,"Role editor is not bound to the authoritative row");
            Check(Children<TextBlock>(roleEditor).Any(text=>text.Text=="乐谱使用者"),"Dark role selection displays an object type instead of its label");
            Check(Children<TextBlock>((ComboBox)window.FindName("LeaderBox")).Any(text=>text.Text=="host"),"Dark conductor selection lost DisplayMemberPath");
            grid.SelectedIndex=1;
            var images=Environment.GetEnvironmentVariable("MABISYNC_UI_OUTPUT");
            if(images is not null){Directory.CreateDirectory(images);Render(window,1180,900,Path.Combine(images,"ensemble-host.png"));((ScrollViewer)window.FindName("WorkflowScroll")).ScrollToVerticalOffset(250);Render(window,1180,900,Path.Combine(images,"ensemble-roster.png"));Render(window,780,700,Path.Combine(images,"ensemble-compact.png"));((ScrollViewer)window.FindName("WorkflowScroll")).ScrollToBottom();Render(window,1180,900,Path.Combine(images,"roster-wide.png"));Render(window,780,700,Path.Combine(images,"roster-narrow.png"));
                foreach(var expander in Children<Expander>((DependencyObject)window.Content))if(expander.Header?.ToString()?.StartsWith("本机时间微调")==true)expander.IsExpanded=true;
                Render(window,1180,1000,Path.Combine(images,"timing-expanded.png"));
                var capture=(Window)Activator.CreateInstance(_windowType.Assembly.GetType("MabiSync.App.CaptureChannelWindow")!,[null,Field<object>(window,"_gameObservation")])!;
                try{Render(capture,750,650,Path.Combine(images,"capture-dark.png"));}finally{capture.Close();}
                ((CheckBox)window.FindName("EnableGameInput")).IsChecked=true;
                ((ScrollViewer)window.FindName("WorkflowScroll")).ScrollToVerticalOffset(250);
                Render(window,1180,1000,Path.Combine(images,"local-preparation-dark.png"));
                ((CheckBox)window.FindName("EnableGameInput")).IsChecked=false;
                foreach(var name in new[]{"RoomSection","ModeSection","LocalActionSection","RosterSection","StatusSection"})((Expander)window.FindName(name)).IsExpanded=false;
                ((Slider)window.FindName("TimingOffsetSlider")).Value=-125;((ScrollViewer)window.FindName("WorkflowScroll")).ScrollToTop();
                Render(window,780,700,Path.Combine(images,"collapsed-cards.png"));
                Check(((TextBlock)window.FindName("StatusTimingSummary")).Visibility==Visibility.Visible && ((TextBlock)window.FindName("StatusTimingSummary")).Text.Contains("-125"),"Collapsed status hides the timing result");
                foreach(var name in new[]{"RoomSection","ModeSection","LocalActionSection","RosterSection","StatusSection"})((Expander)window.FindName(name)).IsExpanded=true;
                ((Slider)window.FindName("TimingOffsetSlider")).Value=0;
            }
            var local=Field<StopwatchClock>(window,"_clock");var roomClock=Field<RoomClock>(window,"_roomClock");var t=local.NowMs;roomClock.AddSample(t,t,t,t);
            object Action(string id,string kind,double target)=>new{id,sessionId="countdown",kind,targetServerMs=target,serverEpoch="epoch"};
            SetSnapshot(client,["host","member"],new {id="countdown",mode="ensemble",phase="committed",leaderId="host",participantIds=new[]{"host","member"},
                countdownMs=5000,startAction=Action("start-progress","start",t+5000),stopAction=Action("stop-progress","stop",t+35000),stopCommitted=true,interruption=(object?)null,receipts=Array.Empty<object>()});
            Call(window,"Refresh");Check(((FrameworkElement)window.FindName("CountdownPanel")).Visibility==Visibility.Visible && ((ProgressBar)window.FindName("CountdownProgress")).Value>.9,"Scheduled start countdown is not visible or has wrong progress");
            if(images is not null)Render(window,1180,900,Path.Combine(images,"countdown.png"));
            var readyState=new MabiSync.Client.TunnelState("ready",new Uri("wss://fixture.trycloudflare.com"),"公网已就绪");
            typeof(QuickTunnel).GetField("_state",Private)!.SetValue(Field<QuickTunnel>(window,"_tunnel"),readyState);Call(window,"TunnelChanged",readyState);
            Check(((FrameworkElement)window.FindName("FeedbackBanner")).Visibility==Visibility.Visible && ((TextBlock)window.FindName("MessageText")).Text.Contains("公网连接已建立") && ((FrameworkElement)window.FindName("NetworkBanner")).Visibility==Visibility.Collapsed && ((Button)window.FindName("CopyPublicButton")).IsEnabled,"Public success is missing or duplicated");
            var tries=0;string? invitation=null;
            _windowType.GetField("_clipboardWriterOverride",Private)!.SetValue(window,(Action<string>)(text=>{if(++tries<3)throw new System.Runtime.InteropServices.COMException("busy",unchecked((int)0x800401D0));invitation=text;}));
            await (Task)_windowType.GetMethod("CopyInvitationAsync",Private)!.Invoke(window,null)!;Call(window,"Refresh");
            Check(tries==3 && invitation?.Contains("FIXTUR")==true && ((Button)window.FindName("CopyPublicButton")).Content.ToString()!.Contains("已复制"),"Invitation copy lacks inline success");
            Check(((TextBlock)window.FindName("CopyStatusText")).Text.Contains("已复制"),"Copy status was overwritten by room refresh");
            _windowType.GetField("_clipboardWriterOverride",Private)!.SetValue(window,(Action<string>)(_=>throw new System.Runtime.InteropServices.COMException("busy",unchecked((int)0x800401D0))));
            await (Task)_windowType.GetMethod("CopyInvitationAsync",Private)!.Invoke(window,null)!;
            Check(((FrameworkElement)window.FindName("FallbackInvitationPanel")).Visibility==Visibility.Visible && ((TextBox)window.FindName("FallbackInvitationBox")).Text==invitation,"Clipboard contention has no manual invitation fallback");
            Console.WriteLine("PASS WPF copy success stays visible and clipboard contention offers manual invitation");
            var errorState=new MabiSync.Client.TunnelState("error",null,"公网连接测试故障");typeof(QuickTunnel).GetField("_state",Private)!.SetValue(Field<QuickTunnel>(window,"_tunnel"),errorState);Call(window,"TunnelChanged",errorState);
            Check(((TextBlock)window.FindName("NetworkBannerText")).Text.Contains("故障"),"Public failure lacks explicit status");
            var verifying=new TunnelState("verifying",null,"正在验证公网连接");
            typeof(QuickTunnel).GetField("_state",Private)!.SetValue(Field<QuickTunnel>(window,"_tunnel"),verifying);Call(window,"TunnelChanged",verifying);
            Check(((TextBlock)window.FindName("MessageText")).Text==verifying.Message,"Retry still displays an old public failure");
            typeof(QuickTunnel).GetField("_state",Private)!.SetValue(Field<QuickTunnel>(window,"_tunnel"),readyState);Call(window,"TunnelChanged",readyState);
            Call(window,"TunnelChanged",errorState);
            Check(((TextBlock)window.FindName("NetworkBannerText")).Text.Contains("已就绪") && !((TextBlock)window.FindName("MessageText")).Text.Contains("故障"),"Stale public failure replaced a successful connection");
            typeof(RoomClient).GetField("_session",Private)!.SetValue(client,new SessionIdentity("FIXTUR","member","fixture","epoch","ws://127.0.0.1:3132/"));Call(window,"Refresh");
            Check(!(bool)host.GetType().GetProperty("CanEdit")!.GetValue(host)!,"Guest can edit roster");
            Check(!roleEditor.IsEnabled,"Guest still has an interactive role dropdown");
            var pickerType=_windowType.Assembly.GetType("MabiSync.App.ButtonPickerWindow")!;
            var picker=(Window)Activator.CreateInstance(pickerType,Private,null,[Field<object>(window,"_gamePlatform"),new GameWindowId(1,2,3,"fixture.exe"),new ClientArea(-1920,0,1280,960,144),"Ctrl+Alt+F9"],null)!;
            try {
                Check(Children<TextBlock>((DependencyObject)picker.Content).Any(text=>text.Text.Contains("Ctrl+Alt+F9")),"Picker does not display the fallback cancel shortcut");
                Check(picker.Topmost && !picker.ShowActivated && !picker.ShowInTaskbar,"Picker steals initial focus or lacks topmost hit surface");
                using var cancelled=new CancellationTokenSource();cancelled.Cancel();
                try{await (Task)pickerType.GetMethod("PickAsync",Private)!.Invoke(picker,[cancelled.Token])!;throw new InvalidOperationException("Cancelled picker displayed");}
                catch(OperationCanceledException){}
                Check(!picker.IsVisible,"Cancelled point selection left an overlay visible");
            } finally {picker.Close();}
            Console.WriteLine("PASS WPF host readiness, status card, progress and instrument column");
        } finally {typeof(RoomClient).GetField("_connected",Private)!.SetValue(client,false);await Close(window);}
        static bool IsDescendant(DependencyObject child,DependencyObject parent) {for(DependencyObject? p=child;p is not null;p=LogicalTreeHelper.GetParent(p)??System.Windows.Media.VisualTreeHelper.GetParent(p))if(ReferenceEquals(p,parent))return true;return false;}
    }
    private static async Task GrowingRosterAndJoinerBindings()
    {
        var window=NewWindow();var client=Field<RoomClient>(window,"_client");
        try {
            typeof(RoomClient).GetField("_session",Private)!.SetValue(client,new SessionIdentity("FIXTUR","host","fixture","epoch","ws://127.0.0.1:3132/"));
            SetSnapshot(client,["host","member"]);Call(window,"Refresh");Layout(window,1180,900);
            var grid=(DataGrid)window.FindName("MembersGrid");var smallHeight=grid.ActualHeight;
            SetSnapshot(client,["host",..Enumerable.Range(1,23).Select(i=>"member-"+i)]);Call(window,"Refresh");
            foreach(var width in new[]{780,1180}) {
                Layout(window,width,900);
                Check(grid.ActualHeight>=grid.MinRowHeight*24+grid.ColumnHeaderHeight && grid.ActualHeight>smallHeight,"24-member roster is clipped instead of expanding its card");
                Check(ScrollViewer.GetVerticalScrollBarVisibility(grid)==ScrollBarVisibility.Disabled,"Roster still has a nested vertical scrollbar");
            }
            var images=Environment.GetEnvironmentVariable("MABISYNC_UI_OUTPUT");
            if(images is not null){((ScrollViewer)window.FindName("WorkflowScroll")).ScrollToBottom();Render(window,1180,900,Path.Combine(images,"roster-24-members.png"));}
            SetSnapshot(client,["host","member"]);
            typeof(RoomClient).GetField("_session",Private)!.SetValue(client,new SessionIdentity("FIXTUR","member","fixture","epoch","ws://127.0.0.1:3132/"));
            Call(window,"Refresh");((CheckBox)window.FindName("EnableGameInput")).IsChecked=true;Layout(window,1180,900);
            Check(((FrameworkElement)window.FindName("StartBindingControls")).Visibility==Visibility.Visible,"Manual joiner cannot edit its saved start binding");
            Check(((TextBlock)window.FindName("StartRoleHint")).Text.Contains("参与者不触发") && ((TextBlock)window.FindName("StartRoleHint")).Visibility==Visibility.Visible,"Manual joiner has no clear start suppression label");
            ((ComboBox)window.FindName("StartInputKind")).SelectedIndex=0;
            ((TextBox)window.FindName("StartKeyBox")).Text="F8";
            Call(window,"InputChanged"); // ShortcutKeyDown invokes this after recording the key.
            Check(Field<Dictionary<string,ModeBindings>>(window,"_modeBindings")["ensemble"].Start is KeyBinding {Key:0x77},"Manual joiner start binding edit was discarded");
            var capabilities=JsonSerializer.SerializeToElement(_windowType.GetMethod("LocalCapabilities",Private)!.Invoke(window,null));
            Check(!capabilities.GetProperty("canStart").GetBoolean(),"Editing a joiner's start binding enabled its automatic start");
            Call(window,"Refresh");Call(window,"RenderBindings");
            Check(((TextBox)window.FindName("StartKeyBox")).Text=="F8","Room refresh lost the joiner's binding");
            if(images is not null)Render(window,1180,1000,Path.Combine(images,"joiner-start-settings.png"));
            typeof(RoomClient).GetField("_session",Private)!.SetValue(client,new SessionIdentity("FIXTUR","host","fixture","epoch","ws://127.0.0.1:3132/"));Call(window,"Refresh");
            Check(((FrameworkElement)window.FindName("StartRoleHint")).Visibility==Visibility.Collapsed,"Initiator still displays joiner suppression");
            ((ComboBox)window.FindName("ModeBox")).SelectedIndex=0;
            Check(((FrameworkElement)window.FindName("StartRoleHint")).Visibility==Visibility.Collapsed,"Joiner suppression leaked into emote mode");
            Console.WriteLine("PASS WPF roster grows for 24 members; manual joiner can save a start binding without enabling automatic start");
        }finally{await Close(window);}
    }
    private static async Task LongScoreDisplay()
    {
        var window=NewWindow();var client=Field<RoomClient>(window,"_client");
        try {
            typeof(RoomClient).GetField("_session",Private)!.SetValue(client,new SessionIdentity("FIXTUR","host","fixture","epoch","ws://127.0.0.1:3132/"));
            var score=new {title="Long fixture",wholeScoreDurationSeconds=601.25,selectedDurationSeconds=200.5,issue="Fixture estimate"};
            var room=JsonSerializer.SerializeToElement(new {code="FIXTUR",mode="ensemble",hostId="host",leaderId="host",performance=(object?)null,observedCharacters=Array.Empty<object>(),
                members=new[]{new {id="host",nickname="host",role="host",connected=true,ready=false,ensembleGroup=1,ensembleOrder=1,ensembleRole="initiator",gameIdentity=(object?)null,music=score}}});
            var type=typeof(RoomClient).GetNestedType("Snapshot",BindingFlags.NonPublic)!;
            typeof(RoomClient).GetField("_snapshot",Private)!.SetValue(client,Activator.CreateInstance(type,[room]));
            typeof(RoomClient).GetField("_connected",Private)!.SetValue(client,true);
            Call(window,"Refresh");Layout(window,1180,900);
            var display=((TextBlock)window.FindName("ScoreObservationText")).Text;
            Check(display.Contains("601.25") && display.Contains("200.5"),"Long score display capped or confused full-score and selected-track durations");
            Console.WriteLine("PASS WPF long score displays full and selected durations above 200 seconds without capping");
        }finally{typeof(RoomClient).GetField("_connected",Private)!.SetValue(client,false);await Close(window);}
    }
    private static IEnumerable<T> Children<T>(DependencyObject root) where T:DependencyObject
    {
        for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++) {
            var child=System.Windows.Media.VisualTreeHelper.GetChild(root,i);if(child is T result)yield return result;
            foreach(var nested in Children<T>(child))yield return nested;
        }
    }
    private static void Layout(Window window,double width,double height)
    {
        var content=(FrameworkElement)window.Content;window.Width=width;window.Height=height;
        if(window.FindName("StatusCard") is FrameworkElement status)status.MaxHeight=Math.Max(200,height*.48);
        content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
    }
    private static void Render(Window window,int width,int height,string path)
    {
        Layout(window,width,height);var image=new System.Windows.Media.Imaging.RenderTargetBitmap(width,height,96,96,System.Windows.Media.PixelFormats.Pbgra32);
        var background=new System.Windows.Media.DrawingVisual();using(var drawing=background.RenderOpen())drawing.DrawRectangle(window.Background,null,new Rect(0,0,width,height));image.Render(background);
        image.Render((System.Windows.Media.Visual)window.Content);var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));using var output=File.Create(path);png.Save(output);
    }
    private static async Task LanUi(string endpoint)
    {
        var host=NewWindow();var guest=NewWindow();
        var hc=Field<RoomClient>(host,"_client");var gc=Field<RoomClient>(guest,"_client");
        try {
            await hc.ConnectAsync(endpoint);await hc.CreateRoomAsync("LAN host");
            ((ComboBox)guest.FindName("PublicProviderBox")).SelectedIndex=1;
            ((ComboBox)guest.FindName("LanAddressBox")).Text=endpoint;
            new System.Windows.Interop.WindowInteropHelper(guest).EnsureHandle();
            ((Button)guest.FindName("LanTutorialButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var guide=Field<Window>(guest,"_lanTutorial");Check(guide.IsVisible,"LAN guide did not open");guide.Close();
            ((TextBox)guest.FindName("InvitationBox")).Text=hc.Session!.Code;
            ((Button)guest.FindName("JoinInvitationButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>gc.Session is not null && !Field<bool>(guest,"_connectionBusy"));
            Check(Field<bool>(guest,"_usingLan") && gc.Session!.Code==hc.Session.Code,"LAN address and room-code join failed");
            await (Task)_windowType.GetMethod("CloseOrLeaveRoom",Private)!.Invoke(guest,null)!;
            ((ComboBox)guest.FindName("PublicProviderBox")).SelectedIndex=0;
            ((TextBox)guest.FindName("InvitationBox")).Text=new RoomInvitation(new Uri(endpoint),hc.Session.Code).ToString();
            ((Button)guest.FindName("JoinInvitationButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>gc.Session is not null && !Field<bool>(guest,"_connectionBusy"));
            Check(((ComboBox)guest.FindName("PublicProviderBox")).SelectedIndex==1,"LAN invite did not select LAN mode");
            await hc.LeaveRoomAsync(true);await Wait(()=>gc.Session is null);
            ((TextBox)guest.FindName("InvitationBox")).Text="MabiSync/2|old-invite";
            ((Button)guest.FindName("JoinInvitationButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>!Field<bool>(guest,"_connectionBusy"));
            Check(gc.Session is null && ((TextBlock)guest.FindName("MessageText")).Text.Contains("已移除 EasyTier"),"Old EasyTier invite has no migration feedback");
            var settings=Field<string>(guest,"_settings");Call(guest,"SaveSettings");
            var json=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settings))!;json["publicProvider"]="easytier";File.WriteAllText(settings,json.ToJsonString());
            var restored=(Window)Activator.CreateInstance(_windowType,Private,null,[settings,false],null)!;
            try {Check(((ComboBox)restored.FindName("PublicProviderBox")).SelectedIndex==2,"Old provider setting did not migrate to server default");}finally{await Close(restored);}
            Console.WriteLine("PASS WPF LAN guide, address/code join, invitation recognition and removed-provider migration; desktop inputs: 0");
        }finally{await Close(guest);await Close(host);}
    }
    private static async Task CommunityUi(string endpoint)
    {
        var host=NewWindow();var guest=NewWindow();
        try {
            foreach(var window in new[]{host,guest}) {
                var provider=(ComboBox)window.FindName("PublicProviderBox");
                var item=(ComboBoxItem)provider.Items[2];
                Check(item.Content.ToString()!.Contains("非长期"),"Server option omits its temporary availability");
                var field=_windowType.GetField("_communityEndpoint",Private)!;
                Check(((Uri)field.GetValue(window)!).Scheme=="wss","Public server is not encrypted by default");
                field.SetValue(window,new Uri(endpoint));provider.SelectedIndex=2;
                Check(((TextBlock)window.FindName("PublicProviderHint")).Text.Contains("非长期"),"Server choice has no availability hint");
            }
            var hc=Field<RoomClient>(host,"_client");var gc=Field<RoomClient>(guest,"_client");
            ((Button)host.FindName("QuickCreateButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>hc.Session is not null && !Field<bool>(host,"_connectionBusy"));Call(host,"Refresh");
            Check(hc.Room?.GetProperty("mode").GetString()=="ensemble","Created room lost the requested default mode");
            Check(new Uri(hc.Endpoint)==new Uri(endpoint),"Server creation started a local room instead");
            Check(!((ComboBox)host.FindName("PublicProviderBox")).IsEnabled,"Active room can switch connection providers");
            var invitation=(string)_windowType.GetProperty("CurrentInvitation",Private)!.GetValue(host)!;
            Check(RoomInvitation.Parse(invitation).Endpoint==new Uri(endpoint),"Copied server invitation has the wrong address");
            ((TextBox)guest.FindName("InvitationBox")).Text=hc.Session!.Code;
            ((Button)guest.FindName("JoinInvitationButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>gc.Session is not null && !Field<bool>(guest,"_connectionBusy"));Call(guest,"Refresh");
            Check(gc.Session!.Code==hc.Session.Code,"Six-digit server room code did not join the room");
            var memberId=gc.Session.MemberId;await gc.DisconnectAsync();
            await (Task)_windowType.GetMethod("ReconnectRoom",Private)!.Invoke(guest,null)!;
            Check(gc.Connected && gc.Session?.MemberId==memberId,"Server reconnect lost member identity");
            await (Task)_windowType.GetMethod("CloseOrLeaveRoom",Private)!.Invoke(guest,null)!;
            ((ComboBox)guest.FindName("PublicProviderBox")).SelectedIndex=0;
            ((TextBox)guest.FindName("InvitationBox")).Text=invitation;
            ((Button)guest.FindName("JoinInvitationButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>gc.Session is not null && !Field<bool>(guest,"_connectionBusy"));
            Check(((ComboBox)guest.FindName("PublicProviderBox")).SelectedIndex==2,"Server invitation was mislabeled as Cloudflare");
            await (Task)_windowType.GetMethod("CloseOrLeaveRoom",Private)!.Invoke(host,null)!;
            await Wait(()=>gc.Session is null);Call(host,"Refresh");
            Check(!((Button)host.FindName("CopyPublicButton")).IsEnabled,"Closed server room still shares an invitation");
            Console.WriteLine("PASS WPF optional encrypted server entry: create, room code, invitation, reconnect and full closure; desktop inputs: 0");
        }finally{await Close(guest);await Close(host);}
    }
    private static async Task LiveRoom(string endpoint)
    {
        var window=NewWindow(); var client=Field<RoomClient>(window,"_client");
        var otherClock=new StopwatchClock();var otherRoomClock=new RoomClock(otherClock);using var otherScheduler=new ActionScheduler(otherClock,otherRoomClock,new SimulationInput(otherClock));
        await using var guest=new RoomClient(otherClock,otherRoomClock,otherScheduler);
        try
        {
            await client.ConnectAsync(endpoint); await client.CreateRoomAsync("Desktop fixture");
            await Wait(()=>client.Room?.GetProperty("members").GetArrayLength()==1);
            Call(window,"Refresh");
            ((ComboBox)window.FindName("ModeBox")).SelectedIndex=1;
            await Wait(()=>client.Room?.GetProperty("mode").GetString()=="ensemble");
            await guest.ConnectAsync(endpoint);await guest.JoinRoomAsync("Guest fixture",client.Session!.Code);
            await Wait(()=>client.Room?.GetProperty("members").GetArrayLength()==2);Call(window,"Refresh");
            var grid=(DataGrid)window.FindName("MembersGrid");var guestRow=grid.Items.Cast<object>().Single(r=>(string)r.GetType().GetProperty("Id")!.GetValue(r)! == guest.Session!.MemberId);
            // A custom popup may release focus before SelectionChanged is delivered.
            // Exercise the actual role selector without keyboard focus, keeping the group unchanged.
            Layout(window,1180,900);
            var editor=Children<ComboBox>(grid).Single(box=>box.SelectedValuePath=="Value" && ReferenceEquals(box.DataContext,guestRow));
            editor.SelectedValue="initiator";
            await Wait(()=>guest.Room?.GetProperty("members").EnumerateArray().Any(m=>m.GetProperty("id").GetString()==guest.Session!.MemberId && m.GetProperty("ensembleRole").GetString()=="initiator")==true);
            Check(guest.Room!.Value.GetProperty("members").EnumerateArray().Single(m=>m.GetProperty("id").GetString()==client.Session!.MemberId).GetProperty("ensembleRole").GetString()=="joiner","Same-group role handoff did not demote the previous score user");
            await Wait(()=>Field<int>(window,"_rosterEdits")==0);
            Check(((TextBlock)window.FindName("MessageText")).Text.Contains("参与者"),"Role replacement has no explanation");
            Layout(window,1180,900);
            editor=Children<ComboBox>(grid).Single(box=>box.SelectedValuePath=="Value" && ReferenceEquals(box.DataContext,guestRow));
            editor.SelectedValue="joiner";
            await Wait(()=>guest.Room?.GetProperty("members").EnumerateArray().Any(m=>m.GetProperty("id").GetString()==guest.Session!.MemberId && m.GetProperty("ensembleRole").GetString()=="joiner")==true);
            await client.UpdateRosterAsync("edit-ensemble-member",client.Session!.MemberId,musicRole:"initiator");Call(window,"Refresh");
            var number=new TextBox{DataContext=guestRow,Tag="group",Text="2"};
            await (Task)_windowType.GetMethod("CommitRosterNumber",Private)!.Invoke(window,[number])!;
            await Wait(()=>guest.Room?.GetProperty("members").EnumerateArray().Any(m=>m.GetProperty("id").GetString()==guest.Session!.MemberId && m.GetProperty("ensembleGroup").GetInt32()==2)==true);
            var hostRow=grid.Items.Cast<object>().Single(r=>(string)r.GetType().GetProperty("Id")!.GetValue(r)! == client.Session!.MemberId);
            Check(hostRow.GetType().GetProperty("GroupBackground")!.GetValue(hostRow)!.ToString()!=guestRow.GetType().GetProperty("GroupBackground")!.GetValue(guestRow)!.ToString(),"Different groups have identical group-column colors");
            Check(number.Text=="2","Committed group editor reverted to stale state");
            await client.UpdateRosterAsync("edit-ensemble-member",guest.Session!.MemberId,instrument:"远端自定义乐器");
            await Wait(()=>guest.Room?.GetProperty("members").EnumerateArray().Any(m=>m.GetProperty("instrument").GetString()=="远端自定义乐器")==true);
            await guest.LeaveRoomAsync();await Wait(()=>client.Room?.GetProperty("members").GetArrayLength()==1);Call(window,"Refresh");
            foreach (var choice in Enumerable.Range(0,3)) { ((ComboBox)window.FindName("StopChoice")).SelectedIndex=choice;Call(window,"Refresh"); }
            ((Button)window.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>client.Room?.GetProperty("performance") is {ValueKind:JsonValueKind.Object} p && p.GetProperty("phase").GetString()=="committed");
            Call(window,"Refresh");
            ((Button)window.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>client.Room?.GetProperty("performance").GetProperty("phase").GetString()=="stopped");Call(window,"Refresh");
            ((ComboBox)window.FindName("ModeBox")).SelectedIndex=0;
            await Wait(()=>client.Room?.GetProperty("mode").GetString()=="emote" && Field<int>(window,"_rosterEdits")==0);
            ((TextBox)window.FindName("CountdownBox")).Text="1";
            ((Button)window.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>client.Room?.GetProperty("performance").GetProperty("mode").GetString()=="emote" && client.Room?.GetProperty("performance").GetProperty("phase").GetString()=="stopped");
            Call(window,"Refresh");Check(((TextBlock)window.FindName("PhaseText")).Text is "等待全员就绪" or "全员已就绪","Ended round did not return to room readiness");
            await client.SendAsync(new {type="leave"}); await Wait(()=>client.Session is null);
            Call(window,"Refresh");
            await client.CreateRoomAsync("Second fixture");await Wait(()=>client.Room?.GetProperty("members").GetArrayLength()==1);
            await guest.JoinRoomAsync("Rejoined fixture",client.Session!.Code);await Wait(()=>client.Room?.GetProperty("members").GetArrayLength()==2);
            Call(window,"Refresh");
            ((Button)window.FindName("LeaveRoomButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>client.Session is null && guest.Session is null);Call(window,"Refresh");Check(grid.Items.Count==0,"Closed room left stale roster rows");
            await client.DisconnectAsync();Call(window,"Refresh");
            Console.WriteLine("PASS WPF direct number edit, custom instruments, remote broadcast and room closure ejection");
            Console.WriteLine("PASS WPF create, stop choices, performance, leave, second room and disconnect");
        }
        finally {await Close(window);}
    }
    private static async Task RecoveryUi(string endpoint)
    {
        var host=NewWindow();var guest=NewWindow();var owner=Field<RoomClient>(host,"_client");var member=Field<RoomClient>(guest,"_client");
        try {
            await owner.ConnectAsync(endpoint);await owner.CreateRoomAsync("Recovery host");
            await member.ConnectAsync(endpoint);await member.JoinRoomAsync("Recovery member",owner.Session!.Code);
            await Wait(()=>member.Room?.GetProperty("members").GetArrayLength()==2);
            var memberId=member.Session!.MemberId;
            await member.DisconnectAsync();((TextBlock)guest.FindName("MessageText")).Text="房间连接已中断";
            Call(guest,"Refresh");var reconnect=guest.FindName("ReconnectRoomButton") as Button;
            Check(reconnect is{Visibility:Visibility.Visible,IsEnabled:true},"Disconnected member has no direct reconnect action");
            reconnect!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>member.Connected && !Field<bool>(guest,"_connectionBusy") && member.Room?.GetProperty("members").EnumerateArray().Any(m=>m.GetProperty("id").GetString()==memberId && m.GetProperty("connected").GetBoolean())==true);
            await Task.Yield();Call(guest,"Refresh");
            Check(((TextBlock)guest.FindName("MessageText")).Text.Contains("重新连接"),"Successful room resume retained stale disconnection feedback");
            await owner.DisconnectAsync();Call(host,"Refresh");
            ((Button)host.FindName("LeaveRoomButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>owner.Session is null && member.Session is null && !Field<bool>(host,"_connectionBusy"));
            Console.WriteLine("PASS WPF resumed member replaces stale error and disconnected host closes the restored room");
        }finally{await Close(guest);await Close(host);}
    }
    private static async Task OfflineCloseUi()
    {
        var window=NewWindow();var client=Field<RoomClient>(window,"_client");
        var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);
        listener.Start();var port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        var info=new System.Diagnostics.ProcessStartInfo("node"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(_appPath)!};
        info.ArgumentList.Add(Path.Combine(Path.GetDirectoryName(_appPath)!,"Service","server","index.js"));
        info.Environment["MABISYNC_HOST"]="127.0.0.1";info.Environment["MABISYNC_PORT"]=port.ToString();
        using var server=System.Diagnostics.Process.Start(info)!;
        try {
            await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await client.ConnectAsync($"ws://127.0.0.1:{port}");await client.CreateRoomAsync("Offline close host");
            await Wait(()=>client.Room is not null);server.Kill(true);await server.WaitForExitAsync();await Wait(()=>!client.Connected);Call(window,"Refresh");
            ((Button)window.FindName("LeaveRoomButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(7));
            while(Field<bool>(window,"_connectionBusy"))await Task.Delay(20,timeout.Token);
            Check(client.Session is null && client.Room is null,"Unreachable service trapped the host in its old room");
            Check(((Button)window.FindName("QuickCreateButton")).IsEnabled,"Offline room closure left create disabled");
            Console.WriteLine("PASS WPF unreachable host service still releases the room UI for a new room");
        }finally{if(!server.HasExited)server.Kill(true);await server.WaitForExitAsync();await Close(window);}
    }
    private static async Task EarlyEnsembleUi(string endpoint)
    {
        var host=NewWindow();var guest=NewWindow();var client=Field<RoomClient>(host,"_client");var member=Field<RoomClient>(guest,"_client");
        try {
            await client.ConnectAsync(endpoint);await client.CreateRoomAsync("UI timing host");
            await Wait(()=>client.Room?.GetProperty("members").GetArrayLength()==1);Call(host,"Refresh");
            ((ComboBox)host.FindName("ModeBox")).SelectedIndex=1;
            await Wait(()=>client.Room?.GetProperty("mode").GetString()=="ensemble");
            await member.ConnectAsync(endpoint);await member.JoinRoomAsync("UI early member",client.Session!.Code);
            await Wait(()=>member.Room?.GetProperty("members").GetArrayLength()==2);
            ((TextBox)host.FindName("CountdownBox")).Text="1";
            foreach(var initiator in new[]{true,false}) {
                await client.UpdateRosterAsync("edit-ensemble-member",member.Session!.MemberId,group:initiator?2:1,musicRole:initiator?"initiator":"joiner");
                await Wait(()=>member.Room?.GetProperty("members").EnumerateArray().Any(m=>m.GetProperty("id").GetString()==member.Session!.MemberId && m.GetProperty("ensembleRole").GetString()==(initiator?"initiator":"joiner"))==true);
                Call(host,"Refresh");Call(guest,"Refresh");
                ((TextBox)guest.FindName("TimingOffsetBox")).Text=initiator?"-2000":"-125";
                ((Button)guest.FindName("ReadyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Wait(()=>client.Room?.GetProperty("members").EnumerateArray().Any(m=>m.GetProperty("id").GetString()==member.Session!.MemberId && m.GetProperty("ready").GetBoolean())==true);
                Call(host,"Refresh");
                var previous=client.Room?.GetProperty("performance");var previousId=previous is{ValueKind:JsonValueKind.Object}?previous.Value.GetProperty("id").GetString():null;
                ((Button)host.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Wait(()=>client.Room?.GetProperty("performance") is{ValueKind:JsonValueKind.Object} p && p.GetProperty("id").GetString()!=previousId && p.GetProperty("phase").GetString()=="performing");
                Call(host,"Refresh");Call(guest,"Refresh");await Task.Delay(150);
                var round=client.Room!.Value.GetProperty("performance");
                Check(round.GetProperty("stopRule").GetString()=="first-cycle" && (!round.TryGetProperty("stopAction",out var stop) || stop.ValueKind==JsonValueKind.Null),"First cycle mode invented an early stop");
                Check(round.GetProperty("receipts").EnumerateArray().Count(r=>r.GetProperty("status").GetString()=="executed")== (initiator?2:1),"Early member lost its start or manual joiner received an input");
                Check(client.Room.Value.GetProperty("performance").GetProperty("phase").GetString()=="performing","Early UI member stopped the round");
                ((Button)host.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Wait(()=>member.Room?.GetProperty("performance").GetProperty("phase").GetString()=="stopped");Call(host,"Refresh");Call(guest,"Refresh");
            }
            Console.WriteLine("PASS WPF negative offsets, initiator/manual joiner and consecutive first-cycle rounds do not stop on start");
        }finally {await Close(guest);await Close(host);}
    }
    private static async Task PhaseViews()
    {
        var window=NewWindow();var client=Field<RoomClient>(window,"_client");var count=0;
        typeof(RoomClient).GetField("_session",Private)!.SetValue(client,new SessionIdentity("FIXTUR","host","fixture","epoch","ws://127.0.0.1:3132/"));
        try
        {
            foreach (var mode in new[]{"emote","ensemble","independent"})
            foreach (var phase in new[]{"preparing","committed","performing","interrupted","stopped","cancelled"})
            foreach (var stopRule in new[]{"manual","duration","first-cycle"})
            foreach (var hasStop in new[]{false,true})
            {
                if (mode=="emote" && hasStop) continue;
                ((ComboBox)window.FindName("ModeBox")).SelectedIndex=mode=="emote"?0:1;
                object Action(string id,string kind)=>new {id,sessionId="round",kind,targetServerMs=100000d,serverEpoch="epoch"};
                var performance=new Dictionary<string,object?> { ["id"]="round",["mode"]=mode,["phase"]=phase,["leaderId"]=mode=="emote"?null:"host",
                    ["participantIds"]=new[]{"host"},["startAction"]=Action("start","start"),["earliestStartServerMs"]=100000d,["latestStartServerMs"]=100000d,
                    ["stopRule"]=stopRule,["stopCommitted"]=hasStop && phase!="preparing",["cycleStopStatus"]="waiting-for-score",["countdownMs"]=5000,
                    ["interruption"]=phase=="interrupted"?new {memberId="host",source="game-observation"}:null,["receipts"]=Array.Empty<object>()};
                if (hasStop) performance["stopAction"]=Action("stop","stop"); // Absent property is the production wire form.
                SetSnapshot(client,["host"],performance);Call(window,"Refresh");Call(window,"RefreshStatus");count++;
                if (mode=="emote") Check(((Button)window.FindName("StopButton")).Visibility==Visibility.Collapsed,"Emote exposed a music stop");
            }
            Console.WriteLine($"PASS WPF phase rendering: {count} mode/phase/stop combinations");
        }
        finally {await Close(window);}
    }
    private static async Task ClosingCancelsBeforeCleanup()
    {
        var window=NewWindow();var scheduler=Field<ActionScheduler>(window,"_scheduler");var clock=Field<RoomClock>(window,"_roomClock");
        var local=Field<StopwatchClock>(window,"_clock");scheduler.ChangeEpoch("closing");var t=local.NowMs;clock.AddSample(t,t,t,t);
        var stop=new ScheduledAction("closing-stop","closing-round","stop",local.NowMs+5000,"closing") {Mode="ensemble"};
        Check(scheduler.Prepare(stop).Ready && scheduler.Commit(stop),"Closing fixture stop not armed");
        var capture=Field<object>(window,"_gameObservation");var worker=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.GetType().GetField("_worker",Private)!.SetValue(capture,worker.Task);
        var closed=new TaskCompletionSource();window.Closed+=(_,_)=>closed.TrySetResult();
        try { window.Close();Check(scheduler.LocalDeadline(stop.Id) is null,"Closing left an input armed while awaiting capture cleanup"); }
        finally {worker.TrySetResult();await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));}
        Console.WriteLine("PASS WPF close cancels scheduled inputs before asynchronous cleanup");
    }
    private static async Task DisposedService()
    {
        var type=_windowType.Assembly.GetType("MabiSync.App.LocalService")!;
        var ready=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner=Activator.CreateInstance(type,Private,null,new object[]{(Func<Task<bool>>)(()=>ready.Task)},null)!;
        var start=(Task)type.GetMethod("StartAsync")!.Invoke(owner,[false])!;
        ((IDisposable)owner).Dispose();ready.TrySetResult(true);
        try {await start;throw new InvalidOperationException("Disposed local service completed startup");}
        catch(ObjectDisposedException) { }
        Console.WriteLine("PASS WPF local service startup interrupted by disposal");
    }
    private static void FaultLogs()
    {
        var type=_windowType.Assembly.GetType("MabiSync.App.AppFaultLog")!;var path=Path.Combine(_folder,"faults");
        type.GetMethod("Record",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[new InvalidOperationException("PRIVATE room-token score-body"),"fixture",path]);
        var events=(JsonElement[])type.GetMethod("ReadRecent",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[path])!;
        Check(events.Length==1 && !events[0].GetRawText().Contains("PRIVATE") && events[0].GetProperty("stage").GetString()=="fixture","Fault diagnostic lost its location or retained private message text");
        var old=Path.Combine(path,"errors-20000101.jsonl");File.WriteAllLines(old,Enumerable.Repeat("{\"stage\":\"old\"}",20));File.SetLastWriteTimeUtc(old,DateTime.UtcNow.AddDays(-1));
        events=(JsonElement[])type.GetMethod("ReadRecent",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[path])!;
        Check(events.Length==20 && events[^1].GetProperty("stage").GetString()=="fixture","Recent export preferred an older file over the newest error");
        Console.WriteLine("PASS WPF bounded fault diagnostics omit exception messages");
    }
    private static async Task Wait(Func<bool> predicate)
    { using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(!predicate())await Task.Delay(10,timeout.Token); }
    private static async Task Close(Window window)
    {var done=new TaskCompletionSource();window.Closed+=(_,_)=>done.TrySetResult();window.Close();await done.Task.WaitAsync(TimeSpan.FromSeconds(5));}
}
