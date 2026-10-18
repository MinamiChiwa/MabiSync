using MabiSync.Core;
using MabiSync.Windows;

var tests = new List<(string Name, Action Run)>();
void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
ScheduledAction Action(string kind = "start", string mode = "emote", string id = "a") => new(id, "session", kind, 1000, "epoch") { Mode = mode };
void Add(string name, Action test) => tests.Add((name, test));

Add("Occupied F9 uses an available stop chord and all occupied chords degrade without blocking", () =>
{
    var owned=new List<int>();var modifiers=new uint[]{0,3,6};
    try {
        if(HotkeyFixture.RegisterHotKey(0,0x6810,0x4000,0x78))owned.Add(0x6810);
        using var signaled=new ManualResetEventSlim();
        using(var fallback=new LocalStopHotkey(()=>signaled.Set())) {
            Check(fallback.Registered,"Occupied F9 prevents registering a fallback");
            var shortcut=typeof(LocalStopHotkey).GetProperty("Shortcut")?.GetValue(fallback) as string;
            Check(shortcut is "Ctrl+Alt+F9" or "Ctrl+Shift+F9","Fallback shortcut is missing or is still F9");
            var threadId=(uint)typeof(LocalStopHotkey).GetField("_threadId",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(fallback)!;
            Check(HotkeyFixture.PostThreadMessage(threadId,0x0312,(nuint)LocalStopHotkey.Id,0) && signaled.Wait(1000),"Fallback registration does not dispatch the local stop callback");
        }
        for(var i=1;i<modifiers.Length;i++)if(HotkeyFixture.RegisterHotKey(0,0x6810+i,0x4000|modifiers[i],0x78))owned.Add(0x6810+i);
        using var unavailable=new LocalStopHotkey(()=>{});
        Check(!unavailable.Registered && typeof(LocalStopHotkey).GetProperty("Shortcut")?.GetValue(unavailable) is null,"Unavailable hotkey reports an active stop shortcut");
    }finally{foreach(var id in owned)HotkeyFixture.UnregisterHotKey(0,id);}
});

Add("Point picker uses physical client coordinates across mixed DPI and negative monitors", () =>
{
    var type=typeof(ButtonBinding).Assembly.GetType("MabiSync.Core.CalibrationGeometry");
    Check(type is not null,"Physical point picker geometry is missing");
    var method=type!.GetMethod("Capture")!;
    foreach(uint dpi in new uint[]{96,120,144,192}) {
        var area=new ClientArea(-2560,140,1920,1080,dpi);
        var result=(ButtonBinding)method.Invoke(null,[area,area,-1910,460])!;
        Check(result.X==650 && result.Y==320 && result.Dpi==dpi,"Picker applied DPI scaling twice or lost negative monitor coordinates");
        foreach(var changed in new[]{area with{Left=-2559},area with{Dpi=dpi+1},area with{Width=1919}}) {
            try{method.Invoke(null,[area,changed,-1910,460]);throw new InvalidOperationException("Moving/resizing target accepted stale overlay");}
            catch(System.Reflection.TargetInvocationException e) when(e.InnerException is InvalidOperationException){}
        }
    }
});
Add("Instrument catalog retains defaults and reads deduplicated player additions", () =>
{
    var type=typeof(ButtonBinding).Assembly.GetType("MabiSync.Core.InstrumentCatalog");
    Check(type is not null,"Player instrument catalog is missing");
    var result=(IReadOnlyList<string>)type!.GetMethod("Merge")!.Invoke(null,[new[]{"# comment","竖琴","鲁特琴","竖琴"," ","新乐器"},new[]{"远端乐器"}])!;
    Check(result.Count==17 && result.Contains("2和弦鲁特琴") && result.Contains("夏卢莫管") && result.Contains("远端乐器"),"Defaults/custom/remote instruments were lost or duplicated");
});
Add("Window activation restores the chosen minimized game and rejects a reused identity", () =>
{
    var type=typeof(ButtonBinding).Assembly.GetType("MabiSync.Core.WindowActivation");
    Check(type is not null,"Testable selected-window activation is missing");
    var window=new GameWindowId(4,5,6,"game.exe");var state=new GameWindowState(window,new(0,0,1280,960,144),false,false,true);
    int restores=0,activations=0;
    Func<GameWindowId,GameWindowState?> inspect=_=>state;
    Action<GameWindowId> restore=_=>{restores++;state=state with{Visible=true};};
    Action<GameWindowId> activate=_=>{activations++;state=state with{Foreground=true};};
    var method=type!.GetMethod("ActivateAsync")!;
    ((Task)method.Invoke(null,[window,inspect,restore,activate,CancellationToken.None])!).GetAwaiter().GetResult();
    Check(restores==1 && activations==1 && state.Foreground,"Minimized target was not restored and activated");
    state=state with{Identity=window with{ProcessStartedUtcTicks=7},Foreground=false};
    try {((Task)method.Invoke(null,[window,inspect,restore,activate,CancellationToken.None])!).GetAwaiter().GetResult();throw new Exception("Reused window accepted");}
    catch(InvalidOperationException){}
    Check(activations==1,"Activation reached a different process");
});

Add("Invitation copy retries temporary clipboard contention and explains persistent failure", () =>
{
    var tries = 0; string? copied = null;
    ClipboardCopy.WriteAsync("test-invitation", text => { if (++tries < 3) throw new System.Runtime.InteropServices.COMException("busy", unchecked((int)0x800401D0)); copied = text; }).GetAwaiter().GetResult();
    Check(tries == 3 && copied == "test-invitation", "Transient clipboard contention did not recover");
    tries = 0;
    try { ClipboardCopy.WriteAsync("x", _ => { tries++; throw new System.Runtime.InteropServices.COMException("busy", unchecked((int)0x800401D0)); }).GetAwaiter().GetResult(); throw new Exception("Persistent clipboard failure hidden"); }
    catch (InvalidOperationException error) { Check(error.Message.Contains("剪贴板") && tries == 8, "No useful bounded failure"); }
});

Add("Captured navigation keys and numpad divide use the Windows extended marker", () =>
{
    foreach (ushort key in new ushort[] { 0x21, 0x25, 0x28, 0x2D, 0x2E, 0x6F })
        Check(KeyboardInputFlags.For(key, true) == 1 && KeyboardInputFlags.For(key, false) == 3, "Navigation key lost extended/key-up marker");
    foreach (ushort key in new ushort[] { 0x0D, 0x11, 0x41, 0x61, 0x70 })
        Check(KeyboardInputFlags.For(key, true) == 0 && KeyboardInputFlags.For(key, false) == 2, "Ordinary key got an extended marker");
});

Add("Preparing a keyboard action is inert and dispatch uses its frozen local binding", () =>
{
    var f = new Fixture(); var a = Action();
    Check(f.Executor.Prepare(a).Ready, "Local binding not prepared");
    Check(f.Platform.Events.Count == 0, "Prepare emitted input");
    f.Profile = f.Profile with { Emote = new(new KeyBinding(0x42)) };
    var result = f.Executor.Execute(a, CancellationToken.None);
    Check(result.Status == "executed" && !result.Simulated, "Physical dispatch not reported correctly");
    Check(f.Platform.Events.SequenceEqual(new[] { "key:65:down", "key:65:up" }), "Dispatched changed binding or failed to release");
    Check(f.Executor.Execute(a, CancellationToken.None).Status == "failed", "Completed binding reused");
});
Add("Input rechecks focus at dispatch and never activates an unrelated window", () =>
{
    var f = new Fixture(); var a = Action(); f.Executor.Prepare(a);
    f.Platform.State = f.Platform.State with { Foreground = false };
    Check(f.Executor.Execute(a, CancellationToken.None).Status == "failed", "Lost focus was ignored");
    Check(f.Platform.Events.Count == 0, "Sent input to a different foreground window");
});
Add("Music stop restores its prepared game window before sending a key or button", () =>
{
    foreach(var mode in new[]{"independent","ensemble"})foreach(var minimized in new[]{false,true})foreach(var button in new[]{false,true})
    {
        var f=new Fixture();var binding=button?(GameBinding)new ButtonBinding(200,100,1280,960,144):new KeyBinding(0x42);
        f.Profile=f.Profile with{Independent=new(Stop:binding),Ensemble=new(Stop:binding)};
        var stop=Action("stop",mode);Check(f.Executor.Prepare(stop).Ready,"Stop did not prepare");
        f.Profile=f.Profile with{Window=Fixture.Window with{Handle=999}};
        f.Platform.State=f.Platform.State with{Foreground=false,Visible=!minimized,Minimized=minimized,Client=minimized?new(0,0,0,0,144):f.Platform.State.Client};
        var result=f.Executor.Execute(stop,CancellationToken.None);
        Check(result.Status=="executed", "Background/minimized stop failed: "+result.Detail);
        Check(f.Platform.Activated.Single()==Fixture.Window && f.Platform.State.Foreground,"Stop activated a changed UI selection instead of its prepared game");
        Check(f.Platform.Events.SequenceEqual(button?new[]{"move:300:300","mouse:down","mouse:up"}:new[]{"key:66:down","key:66:up"}),"Stop sent incorrect or duplicate input");
    }
});
Add("Stop activation failure, cancellation and identity mismatch send no input", () =>
{
    foreach(var failure in new[]{"denied","cancelled","identity","permission"})
    {
        var f=new Fixture();var stop=Action("stop","ensemble");Check(f.Executor.Prepare(stop).Ready,"Stop not prepared");
        f.Platform.State=f.Platform.State with{Foreground=false};using var cancel=new CancellationTokenSource();
        if(failure=="denied")f.Platform.DenyActivation=true;
        if(failure=="cancelled")f.Platform.DuringActivation=()=>cancel.Cancel();
        if(failure=="identity")f.Platform.State=f.Platform.State with{Identity=Fixture.Window with{ProcessStartedUtcTicks=9}};
        if(failure=="permission")f.Platform.State=f.Platform.State with{InputPermission=false};
        var result=f.Executor.Execute(stop,cancel.Token);
        Check(result.Status==(failure=="cancelled"?"cancelled":"failed") && f.Platform.Events.Count==0,"Stop bypassed a failed activation or safety check");
        if(failure is "identity" or "permission")Check(f.Platform.Activated.Count==0,"Stop attempted to activate an invalid or inaccessible target");
    }
});
Add("Stop button calibration is rechecked after the game is restored", () =>
{
    var f=new Fixture();f.Profile=f.Profile with{Ensemble=new(Stop:new ButtonBinding(200,100,1280,960,144))};
    var stop=Action("stop","ensemble");Check(f.Executor.Prepare(stop).Ready,"Stop not prepared");
    f.Platform.State=f.Platform.State with{Foreground=false,Visible=false,Minimized=true};
    f.Platform.RestoredArea=new(100,200,1280,960,192);
    var result=f.Executor.Execute(stop,CancellationToken.None);
    Check(f.Platform.Activated.Count==1 && result.Status=="failed" && f.Platform.Events.Count==0,"Restoring a changed DPI bypassed button calibration");
});
Add("Readiness accepts a minimized selected window and revalidates restored geometry before input", () =>
{
    var f=new Fixture();f.Profile=f.Profile with{Ensemble=new(new ButtonBinding(200,100,1280,960,144),new KeyBinding(0x42))};
    f.Platform.State=f.Platform.State with{Visible=false,Foreground=false,Client=new(0,0,0,0,144)};
    var start=Action(mode:"ensemble");Check(f.Executor.Prepare(start).Ready,"Minimized window prevented inert readiness");
    Check(f.Platform.Events.Count==0,"Preparing a minimized window emitted input");
    f.Platform.State=f.Platform.State with{Visible=true,Foreground=true,Client=new(20,30,1281,960,144)};
    Check(f.Executor.Execute(start,CancellationToken.None).Status=="failed" && f.Platform.Events.Count==0,"Restoring at a new size bypassed calibration");
    f.Platform.State=f.Platform.State with{Visible=false,Foreground=false,Client=new(0,0,0,0,144)};
    var next=Action(mode:"ensemble",id:"restored");Check(f.Executor.Prepare(next).Ready,"Second minimized preparation failed");
    f.Platform.State=f.Platform.State with{Visible=true,Foreground=true,Client=new(20,30,1280,960,144)};
    Check(f.Executor.Execute(next,CancellationToken.None).Status=="executed","Restored target did not execute");
});
Add("A reused handle or changed process identity cannot receive a prepared action", () =>
{
    var f = new Fixture(); var a = Action(); f.Executor.Prepare(a);
    f.Platform.State = f.Platform.State with { Identity = f.Platform.State.Identity with { ProcessStartedUtcTicks = 2 } };
    Check(f.Executor.Execute(a, CancellationToken.None).Status == "failed" && f.Platform.Events.Count == 0, "Reused window handle accepted");
});
Add("Permission mismatch blocks preparation and minimized dispatch never sends input", () =>
{
    foreach (var permission in new[] { false, true })
    {
        var f = new Fixture(); var a = Action();
        f.Platform.State = f.Platform.State with { InputPermission = permission, Visible = !permission };
        Check(f.Executor.Prepare(a).Ready==permission && f.Platform.Events.Count == 0,"Permission or minimized readiness was incorrect");
        Check(f.Executor.Execute(a,CancellationToken.None).Status=="failed" && f.Platform.Events.Count==0,"Unusable window received input");
    }
});
Add("Mouse calibration follows window movement but expires after size or DPI changes", () =>
{
    var f = new Fixture(); f.Profile = f.Profile with { Emote = new(new ButtonBinding(200, 100, 1280, 960, 144)) };
    var a = Action(); Check(f.Executor.Prepare(a).Ready, "Calibrated click rejected");
    f.Platform.State = f.Platform.State with { Client = new(-1500, 50, 1280, 960, 144) };
    Check(f.Executor.Execute(a, CancellationToken.None).Status == "executed", "Moving window invalidated valid calibration");
    Check(f.Platform.Events.SequenceEqual(new[] { "move:-1300:150", "mouse:down", "mouse:up" }), "Physical client position mapped incorrectly");
    foreach (var area in new[] { new ClientArea(0, 0, 1281, 960, 144), new ClientArea(0, 0, 1280, 960, 192) })
    {
        f.Platform.Events.Clear(); f.Platform.State = f.Platform.State with { Client = new(0, 0, 1280, 960, 144) };
        f.Executor.Prepare(a); f.Platform.State = f.Platform.State with { Client = area };
        Check(f.Executor.Execute(a, CancellationToken.None).Status == "failed" && f.Platform.Events.Count == 0, "Stale calibration clicked");
    }
});
Add("Occluding windows and points outside the physical client area cannot receive a click", () =>
{
    var f = new Fixture(); f.Profile = f.Profile with { Emote = new(new ButtonBinding(200, 100, 1280, 960, 144)) };
    var a = Action(); f.Executor.Prepare(a); f.Platform.PointOwned = false;
    Check(f.Executor.Execute(a, CancellationToken.None).Status == "failed" && f.Platform.Events.Count == 0, "Clicked an overlay");
    f.Profile = f.Profile with { Emote = new(new ButtonBinding(1280, 100, 1280, 960, 144)) };
    Check(!f.Executor.Prepare(a).Ready, "Accepted an out-of-client click");
});
Add("A cancellation after key-down releases the chord in reverse order", () =>
{
    var f = new Fixture(); f.Profile = f.Profile with { Emote = new(new KeyBinding(0x41, Control: true)) };
    using var cancellation = new CancellationTokenSource();
    f.Platform.AfterKeyDown = _ => cancellation.Cancel();
    var a = Action(); f.Executor.Prepare(a);
    Check(f.Executor.Execute(a, cancellation.Token).Status == "cancelled", "Cancellation was ignored");
    Check(f.Platform.Events.SequenceEqual(new[] { "key:17:down", "key:17:up" }), "Chord remained held or extra key was sent");
});
Add("Keys already held by the player are neither pressed nor released", () =>
{
    var f = new Fixture(); f.Platform.Held.Add(0x41); var a = Action(); f.Executor.Prepare(a);
    Check(f.Executor.Execute(a, CancellationToken.None).Status == "failed", "Held player key was overwritten");
    f.Executor.ReleaseAll(); Check(f.Platform.Events.Count == 0, "Released a key the tool did not own");
});
Add("A failed input API still releases keys pressed by this action", () =>
{
    var f = new Fixture(); f.Profile = f.Profile with { Emote = new(new KeyBinding(0x41, Control: true)) };
    f.Platform.FailKey = 0x41; var a = Action(); f.Executor.Prepare(a);
    Check(f.Executor.Execute(a, CancellationToken.None).Status == "failed", "Input API failure was reported as executed");
    Check(f.Platform.Events.Last() == "key:17:up", "Modifier was left held after API failure");
});
Add("Toggle stop requires current matching playback evidence; explicit stop remains available", () =>
{
    foreach (var observation in new PlaybackObservation?[] { null, new(Fixture.Window, "other", true, 100), new(Fixture.Window, "session", true, -1), new(Fixture.Window, "session", false, 100), new(Fixture.Window with { ProcessId = 99 }, "session", true, 100) })
    {
        var f = new Fixture(); f.Profile = f.Profile with { Independent = new(new KeyBinding(0x41), new KeyBinding(0x41), true) };
        f.Evidence.Value = observation; var a = Action("stop", "independent");
        Check(f.Executor.Prepare(a).Ready, "Stop cannot be prepared before its start");
        Check(f.Executor.Execute(a, CancellationToken.None).Status == "cancelled" && f.Platform.Events.Count == 0, "Unverified toggle could start playing again");
    }
    var good = new Fixture(); good.Profile = good.Profile with { Independent = new(new KeyBinding(0x41), new KeyBinding(0x41), true) };
    good.Evidence.Value = new(Fixture.Window, "session", true, 100); var stop = Action("stop", "independent"); good.Executor.Prepare(stop);
    Check(good.Executor.Execute(stop, CancellationToken.None).Status == "executed", "Reliable playing evidence could not stop");
    var dedicated = new Fixture(); dedicated.Executor.Prepare(stop);
    Check(dedicated.Executor.Execute(stop, CancellationToken.None).Status == "executed", "Dedicated stop depended on toggle evidence");
});
Add("Emotes never use music stop and invalid bindings are rejected", () =>
{
    var f = new Fixture(); Check(!f.Executor.Prepare(Action("stop")).Ready, "Emote acquired a stop input");
    foreach (var key in new ushort[] { 0, 0x10, 0x11, 0x12, 0x5b, 0x78, 0xffff })
    {
        f.Profile = f.Profile with { Emote = new(new KeyBinding(key)) };
        Check(!f.Executor.Prepare(Action()).Ready, "Invalid or reserved binding accepted");
    }
});
Add("Virtual desktop normalization supports negative origins and physical endpoints", () =>
{
    Check(DesktopCoordinates.Normalize(-1920, 0, -1920, 0, 3840, 2160) == (0, 0), "Negative desktop origin mapped incorrectly");
    Check(DesktopCoordinates.Normalize(1919, 2159, -1920, 0, 3840, 2160) == (65535, 65535), "Last physical pixel mapped incorrectly");
    Check(DesktopCoordinates.Normalize(0, 0, -100, -50, 201, 101) == (32768, 32768), "Centered physical coordinate mapped incorrectly");
    try { DesktopCoordinates.Normalize(1920, 0, -1920, 0, 3840, 2160); throw new InvalidOperationException("Outside desktop accepted"); }
    catch (ArgumentOutOfRangeException) { }
});
Add("Routing keeps prepared physical input on its original executor after a mode switch", () =>
{
    var f = new Fixture(); var simulation = new MabiSync.Client.SimulationInput(new TestClock());
    var router = new InputRouter(simulation, f.Executor) { UseSimulation = false }; var a = Action();
    Check(router.Prepare(a).Ready, "Physical route did not prepare"); router.UseSimulation = true;
    Check(!router.IsSimulation(a), "Route changed after prepare");
    Check(router.Execute(a, CancellationToken.None).Status == "executed" && simulation.Count == 0 && f.Platform.Events.Count == 2, "A physical action was rerouted into a simulation");
    router.Forget(a.Id);
    var b = Action(id: "next"); router.Prepare(b); router.Forget(b.Id);
    Check(router.Execute(b, CancellationToken.None).Status == "failed", "A cancelled route survived Forget");
});
Add("Shortcut text preserves modifier semantics and rejects ambiguous or reserved keys", () =>
{
    Check(ShortcutText.Parse("Ctrl+Shift+1") == new KeyBinding(0x31, Control: true, Shift: true), "Modifiers changed");
    Check(ShortcutText.Parse("F8") == new KeyBinding(0x77), "Function key changed");
    Check(ShortcutText.Parse(" ") is null, "Empty binding became an input");
    foreach (var text in new[] { "F9", "Ctrl+F9", "F0", "Ctrl+Ctrl+1", "Win+A", "Ctrl+" })
    {
        try { ShortcutText.Parse(text); throw new InvalidOperationException("Invalid shortcut accepted: " + text); } catch (FormatException) { }
    }
    var saved = new ModeBindings(new ButtonBinding(12, 34, 800, 600, 144), new KeyBinding(0x31, Control: true), true);
    var restored = System.Text.Json.JsonSerializer.Deserialize<ModeBindings>(System.Text.Json.JsonSerializer.Serialize(saved));
    Check(restored == saved, "Saved polymorphic bindings lost their type or calibration");
});
Add("Music start cannot arm without an available stop path", () =>
{
    var f = new Fixture(); f.Profile = f.Profile with { Independent = new(new KeyBinding(0x41)) };
    Check(!f.Executor.Prepare(Action(mode: "independent")).Ready, "Armed music with no stop binding");
    f.Profile = f.Profile with { Independent = new(new KeyBinding(0x41), new KeyBinding(0x41), true) };
    var unknown = new GameInputExecutor(new TestClock(), f.Platform, () => f.Profile, new UnknownPlaybackEvidence());
    Check(!unknown.Prepare(Action(mode: "independent")).Ready, "Armed an unobservable toggle stop");
});

Add("A prefilled chat draft dispatches exactly one Enter without typing or clicking", () =>
{
    var f = new Fixture(); f.Profile = f.Profile with { Emote = new(new ChatDraftBinding()) };
    var a = Action(); Check(!f.Executor.Prepare(a).Ready, "An unconfirmed draft was armed");
    f.Executor.ConfirmChatDraft(); Check(f.Executor.Prepare(a).Ready, "Chat draft was not prepared");
    Check(f.Platform.Events.Count == 0, "Preparing chat edited the draft");
    Check(f.Executor.Execute(a, CancellationToken.None).Status == "executed", "Chat submit failed");
    Check(f.Platform.Events.SequenceEqual(new[] { "key:13:down", "key:13:up" }), "Chat did more than press Enter once");
    Check(!f.Executor.Prepare(Action(id: "second-round")).Ready, "A new round submitted without a fresh draft confirmation");
    f.Executor.ConfirmChatDraft(); Check(f.Executor.Prepare(Action(id: "third-round")).Ready, "A freshly confirmed draft did not recover");
    var roundtrip = System.Text.Json.JsonSerializer.Deserialize<ModeBindings>(System.Text.Json.JsonSerializer.Serialize(f.Profile.Emote));
    Check(roundtrip == f.Profile.Emote, "Chat choice was not saved correctly");
});
Add("Held modifiers cannot turn chat submit into another command", () =>
{
    foreach (ushort modifier in new ushort[] { 0x10, 0x11, 0x12, 0x5b, 0x5c, 0xa0, 0xa3, 0xa5 })
    {
        var f = new Fixture(); f.Profile = f.Profile with { Emote = new(new KeyBinding(13)) };
        f.Platform.Held.Add(modifier); var a = Action(); f.Executor.Prepare(a);
        Check(f.Executor.Execute(a, CancellationToken.None).Status == "failed", "Held modifier ignored");
        Check(f.Platform.Events.Count == 0, "Modified Enter or player key release was sent");
    }
});
Add("A late stop can prepare during a normal start hold without sending overlapping input", () =>
{
    var f = new Fixture(); var start = Action(mode: "independent"); var stop = Action("stop", "independent", "late-stop");
    InputPreparation? late = null;
    f.Platform.AfterKeyDown = _ => { f.Platform.AfterKeyDown = null; late = f.Executor.Prepare(stop); };
    Check(f.Executor.Prepare(start).Ready, "Start not prepared");
    Check(f.Executor.Execute(start, CancellationToken.None).Status == "executed", "Start failed");
    Check(late?.Ready == true, "Stop preparation mistook the active start for a stuck input");
    Check(f.Platform.Events.SequenceEqual(new[] { "key:65:down", "key:65:up" }), "Stop preparation dispatched input");
    Check(f.Executor.Execute(stop, CancellationToken.None).Status == "executed", "Late stop failed");
    Check(f.Platform.Events.SequenceEqual(new[] { "key:65:down", "key:65:up", "key:66:down", "key:66:up" }), "Start and stop overlapped");
});
Add("Failed releases block new input until the owned key is successfully released", () =>
{
    var f = new Fixture(); f.Platform.FailRelease = true; var a = Action(); f.Executor.Prepare(a);
    var late = Action("stop", "independent", "prepared-during-hold");
    f.Platform.AfterKeyDown = _ => { f.Platform.AfterKeyDown = null; Check(f.Executor.Prepare(late).Ready, "Late inert preparation failed"); };
    Check(f.Executor.Execute(a, CancellationToken.None).Status == "failed", "Release failure hidden");
    Check(!f.Executor.Prepare(Action(id: "blocked")).Ready, "Another action armed with stuck owned keys");
    Check(f.Executor.Execute(late, CancellationToken.None).Status == "failed" && f.Platform.Events.Count == 1, "A previously prepared stop overlapped an unreleased key");
    f.Platform.FailRelease = false; f.Executor.ReleaseAll();
    Check(f.Executor.Prepare(Action(id: "recovered")).Ready, "Successful key release did not recover");
});
Add("Local unready state prevents new starts while keeping music stop available", () =>
{
    var f = new Fixture(); var router = new InputRouter(new MabiSync.Client.SimulationInput(new TestClock()), f.Executor) { UseSimulation = false };
    router.StartsEnabled = false;
    Check(!router.Prepare(Action()).Ready, "An unready participant armed a start");
    Check(router.Prepare(Action("stop", "independent")).Ready, "Unready state blocked music stop");
    router.StartsEnabled = true;
    Check(router.Prepare(Action(id: "ready")).Ready, "Ready state did not restore start preparation");
});

Add("Readiness for an emote cannot authorize a saved music binding", () =>
{
    var f = new Fixture(); var router = new InputRouter(new MabiSync.Client.SimulationInput(new TestClock()), f.Executor) { UseSimulation = false, ReadyMode = "emote" };
    Check(!router.Prepare(Action(mode: "independent")).Ready, "An emote-ready player armed music without preparing it");
    Check(router.Prepare(Action()).Ready, "The selected emote was blocked");
    router.ReadyMode = "independent";
    Check(router.Prepare(Action(mode: "independent", id: "music-ready")).Ready, "New music readiness did not select the music binding");
    router.StartsEnabled = false;
    Check(router.Prepare(Action("stop", "independent", "music-stop")).Ready, "Readiness mode blocked a committed music stop");
    Check(f.Platform.Events.Count == 0, "Readiness checks emitted input");
});

var failed = 0;
foreach (var (name, run) in tests) { try { run(); Console.WriteLine("PASS " + name); } catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); } }
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed"); return failed == 0 ? 0 : 1;

sealed class TestClock : IMonotonicClock { public double NowMs => 0; }
sealed class TestEvidence : IPlaybackEvidence { public bool Available => true; public PlaybackObservation? Value; public PlaybackObservation? Read(string sessionId) => Value; }
sealed class Fixture
{
    public static readonly GameWindowId Window = new(123, 456, 1, "C:/Game/MabinogiMobile.exe");
    public GameInputProfile Profile = new(Window, new(new KeyBinding(0x41)), new(new KeyBinding(0x41), new KeyBinding(0x42)), new(new KeyBinding(0x43), new KeyBinding(0x44)));
    public TestPlatform Platform = new(); public TestEvidence Evidence = new(); public GameInputExecutor Executor;
    public Fixture() { Executor = new(new TestClock(), Platform, () => Profile, Evidence); }
}
sealed class TestPlatform : IGameInputPlatform
{
    public GameWindowState State = new(Fixture.Window, new(100, 200, 1280, 960, 144), true, true, true);
    public List<string> Events = []; public HashSet<ushort> Held = []; public bool PointOwned = true;
    public ushort? FailKey; public bool FailRelease; public Action<ushort>? AfterKeyDown;
    public List<GameWindowId> Activated=[];public bool DenyActivation;public Action? DuringActivation;
    public ClientArea RestoredArea=new(100,200,1280,960,144);
    public Task ActivateAsync(GameWindowId window,CancellationToken token=default)
    {
        Activated.Add(window);DuringActivation?.Invoke();token.ThrowIfCancellationRequested();
        if(!DenyActivation)State=State with{Foreground=true,Visible=true,Minimized=false,Client=State.Minimized?RestoredArea:State.Client};
        return Task.CompletedTask;
    }
    public GameWindowState? Inspect(GameWindowId window) => State;
    public bool KeyIsDown(ushort key) => Held.Contains(key);
    public bool LeftButtonIsDown() => false;
    public bool PointBelongsToWindow(GameWindowId window, int x, int y) => PointOwned;
    public void Key(ushort key, bool down) { if ((down && key == FailKey) || (!down && FailRelease)) throw new IOException("Input refused"); Events.Add($"key:{key}:{(down ? "down" : "up")}"); if (down) AfterKeyDown?.Invoke(key); }
    public void MovePointer(int x, int y) => Events.Add($"move:{x}:{y}");
    public void LeftButton(bool down) => Events.Add("mouse:" + (down ? "down" : "up"));
}

static class HotkeyFixture
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(nint window,int id,uint modifiers,uint key);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(nint window,int id);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool PostThreadMessage(uint threadId,uint message,nuint wparam,nint lparam);
}
