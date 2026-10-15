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

internal static class Program
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _failed;
    private static Type _windowType = null!;
    private static string _folder = null!;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("Supply the application assembly path");
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
            try { await SnapshotRace(); await RoomUi(); await PhaseViews(); await ClosingCancelsBeforeCleanup(); await DisposedService(); FaultLogs(); if (args.Length > 1) await LiveRoom(args[1]); }
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
        var input=(CheckBox)window.FindName("EnableGameInput");
        Check(input.IsChecked==true && !Field<InputRouter>(window,"_inputRouter").UseSimulation,"Game input is not the default active option");
        // Every integration probe explicitly opts into simulation before joining a room.
        input.IsChecked=false;return window;
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
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
            groupEditor.SelectAll();groupEditor.SelectedText="12";Call(window,"Refresh");
            Check(groupEditor.Text=="12","Unchanged room refresh overwrote a typed group draft");groupEditor.GetBindingExpression(TextBox.TextProperty)!.UpdateTarget();
            var roleEditor=Children<ComboBox>(grid).First(box=>box.SelectedValuePath=="Value");
            Check(roleEditor.SelectedValue as string=="initiator" && roleEditor.IsEnabled,"Role editor is not bound to the authoritative row");
            grid.SelectedIndex=1;
            var images=Environment.GetEnvironmentVariable("MABISYNC_UI_OUTPUT");
            if(images is not null){Directory.CreateDirectory(images);Render(window,1180,900,Path.Combine(images,"ensemble-host.png"));((ScrollViewer)window.FindName("WorkflowScroll")).ScrollToVerticalOffset(250);Render(window,1180,900,Path.Combine(images,"ensemble-roster.png"));Render(window,780,700,Path.Combine(images,"ensemble-compact.png"));}
            var local=Field<StopwatchClock>(window,"_clock");var roomClock=Field<RoomClock>(window,"_roomClock");var t=local.NowMs;roomClock.AddSample(t,t,t,t);
            object Action(string id,string kind,double target)=>new{id,sessionId="countdown",kind,targetServerMs=target,serverEpoch="epoch"};
            SetSnapshot(client,["host","member"],new {id="countdown",mode="ensemble",phase="committed",leaderId="host",participantIds=new[]{"host","member"},
                countdownMs=5000,startAction=Action("start-progress","start",t+5000),stopAction=Action("stop-progress","stop",t+35000),stopCommitted=true,interruption=(object?)null,receipts=Array.Empty<object>()});
            Call(window,"Refresh");Check(((FrameworkElement)window.FindName("CountdownPanel")).Visibility==Visibility.Visible && ((ProgressBar)window.FindName("CountdownProgress")).Value>.9,"Scheduled start countdown is not visible or has wrong progress");
            if(images is not null)Render(window,1180,900,Path.Combine(images,"countdown.png"));
            var readyState=new MabiSync.Client.TunnelState("ready",new Uri("wss://fixture.trycloudflare.com"),"公网已就绪");
            typeof(QuickTunnel).GetField("_state",Private)!.SetValue(Field<QuickTunnel>(window,"_tunnel"),readyState);Call(window,"TunnelChanged",readyState);
            Check(((FrameworkElement)window.FindName("NetworkBanner")).Visibility==Visibility.Visible && ((Button)window.FindName("CopyPublicButton")).IsEnabled,"Public ready state lacks visible feedback");
            var errorState=new MabiSync.Client.TunnelState("error",null,"公网连接测试故障");typeof(QuickTunnel).GetField("_state",Private)!.SetValue(Field<QuickTunnel>(window,"_tunnel"),errorState);Call(window,"TunnelChanged",errorState);
            Check(((TextBlock)window.FindName("NetworkBannerText")).Text.Contains("故障"),"Public failure lacks explicit status");
            typeof(RoomClient).GetField("_session",Private)!.SetValue(client,new SessionIdentity("FIXTUR","member","fixture","epoch","ws://127.0.0.1:3132/"));Call(window,"Refresh");
            Check(!(bool)host.GetType().GetProperty("CanEdit")!.GetValue(host)!,"Guest can edit roster");
            Check(!roleEditor.IsEnabled,"Guest still has an interactive role dropdown");
            var pickerType=_windowType.Assembly.GetType("MabiSync.App.ButtonPickerWindow")!;
            var picker=(Window)Activator.CreateInstance(pickerType,Private,null,[Field<object>(window,"_gamePlatform"),new GameWindowId(1,2,3,"fixture.exe"),new ClientArea(-1920,0,1280,960,144)],null)!;
            try {
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
        content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
    }
    private static void Render(Window window,int width,int height,string path)
    {
        Layout(window,width,height);var image=new System.Windows.Media.Imaging.RenderTargetBitmap(width,height,96,96,System.Windows.Media.PixelFormats.Pbgra32);
        var background=new System.Windows.Media.DrawingVisual();using(var drawing=background.RenderOpen())drawing.DrawRectangle(window.Background,null,new Rect(0,0,width,height));image.Render(background);
        image.Render((System.Windows.Media.Visual)window.Content);var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));using var output=File.Create(path);png.Save(output);
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
            var number=new TextBox{DataContext=guestRow,Tag="group",Text="2"};
            await (Task)_windowType.GetMethod("CommitRosterNumber",Private)!.Invoke(window,[number])!;
            await Wait(()=>guest.Room?.GetProperty("members").EnumerateArray().Any(m=>m.GetProperty("id").GetString()==guest.Session!.MemberId && m.GetProperty("ensembleGroup").GetInt32()==2)==true);
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
            await Wait(()=>client.Room?.GetProperty("mode").GetString()=="emote");
            ((TextBox)window.FindName("CountdownBox")).Text="1";
            ((Button)window.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>client.Room?.GetProperty("performance").GetProperty("mode").GetString()=="emote" && client.Room?.GetProperty("performance").GetProperty("phase").GetString()=="stopped");
            Call(window,"Refresh");Check(((TextBlock)window.FindName("PhaseText")).Text=="本轮已结束","Emote phase did not update");
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
