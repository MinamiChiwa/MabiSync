using System.Collections.Concurrent;
using MabiSync.Core;

var tests = new List<(string Name, Func<Task> Run)>();
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
void Near(double actual, double expected, double tolerance = .000001) => Check(Math.Abs(actual - expected) < tolerance, $"Expected {expected}, received {actual}");
void Add(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));

Add("Manual ensemble preparation checks only stop without scheduling or retaining input", () =>
{
    var local = new StopwatchClock(); var room = new RoomClock(local); var input = new PhysicalBoundaryInput();
    using var scheduler = new ActionScheduler(local, room, input); scheduler.ChangeEpoch("join-epoch");
    Check(scheduler.ValidateMusicStopPath("manual-join", "join-epoch", "ensemble").Ready, "Stop-only preparation failed without a clock sample");
    Check(input.Forgotten.Count == 1, "Read-only stop check retained its input path");
    Check(!scheduler.ValidateMusicStopPath("manual-join", "old-epoch", "ensemble").Ready, "Old epoch accepted");
    Check(!scheduler.ValidateMusicStopPath("manual-join", "join-epoch", "emote").Ready, "Emote accepted music session context");
    input.Ready = false;
    Check(!scheduler.ValidateMusicStopPath("manual-join", "join-epoch", "ensemble").Ready, "Missing stop binding was accepted");
});

Add("Four timestamps remove service processing time and expire old samples", () =>
{
    var local = new ManualClock { NowMs = 120 };
    var clock = new RoomClock(local);
    Check(clock.AddSample(100, 1110, 1130, 140), "Valid sample rejected");
    local.NowMs = 140;
    var estimate = clock.Estimate!;
    Near(estimate.OffsetMs, 1000); Near(estimate.RoundTripMs, 20);
    local.NowMs = 10200;
    Check(clock.Estimate is null, "Expired samples remained usable");
});
Add("A new high latency sample does not replace a fresh low latency estimate", () =>
{
    var local = new ManualClock { NowMs = 100 };
    var clock = new RoomClock(local);
    clock.AddSample(0, 1005, 1005, 10);
    clock.AddSample(20, 1090, 1090, 120);
    Near(clock.Estimate!.OffsetMs, 1000);
    Check(!clock.AddSample(200, 1, 2, 199), "Reversed local interval accepted");
    Check(!clock.AddSample(0, 200, 300, 10), "Negative network RTT accepted");
    Check(!clock.AddSample(0, double.NaN, 20, 30), "Nonfinite sample accepted");
    clock.Reset(); Check(clock.Estimate is null, "Epoch reset retained clock samples");
});
tests.Add(("An inert action never executes before commit and duplicate commit executes once", async () =>
{
    var clock = new StopwatchClock(); var room = new RoomClock(clock); var input = new RecordingInput(clock);
    var t = clock.NowMs; room.AddSample(t, t, t, t);
    using var scheduler = new ActionScheduler(clock, room, input);
    scheduler.ChangeEpoch("epoch-a"); t = clock.NowMs; room.AddSample(t, t, t, t);
    var action = new ScheduledAction("start-a", "session-a", "start", clock.NowMs + 180, "epoch-a");
    Check(scheduler.Prepare(action).Ready, "Prepare failed");
    await Task.Delay(30); Check(input.Executed.IsEmpty, "Preparation emitted input");
    Check(scheduler.Commit(action), "Commit failed"); scheduler.Commit(action);
    Check(input.Arrived.Wait(2000), "Committed input never executed");
    await Task.Delay(40); Check(input.Executed.Count == 1, "Duplicate commit repeated input");
    Check(!scheduler.Prepare(action).Ready, "Completed action was accepted again");
}));

Add("Emote flow cannot prepare or dispatch a stop operation", () =>
{
    var clock = new StopwatchClock(); var room = new RoomClock(clock); var input = new RecordingInput(clock);
    using var scheduler = new ActionScheduler(clock, room, input); scheduler.ChangeEpoch("emote-epoch");
    var t = clock.NowMs; room.AddSample(t, t, t, t);
    var stop = new ScheduledAction("invalid-stop", "emote-session", "stop", clock.NowMs + 1000, "emote-epoch") { Mode = "emote" };
    Check(!scheduler.Prepare(stop).Ready, "Emote stop was prepared");
    Check(!scheduler.StopNow("urgent-emote", "emote-session", "emote-epoch", "emote"), "Emote stop was dispatched");
    Check(input.Executed.IsEmpty, "Emote flow sent stop input");
});

Add("Cancelling a physical action retains its input kind and forgets its prepared binding", () =>
{
    var clock = new StopwatchClock(); var room = new RoomClock(clock); var input = new PhysicalBoundaryInput();
    using var scheduler = new ActionScheduler(clock, room, input); scheduler.ChangeEpoch("physical");
    var t = clock.NowMs; room.AddSample(t, t, t, t);
    ActionReceipt? receipt = null; scheduler.Receipt += r => receipt = r;
    var action = new ScheduledAction("physical-cancel", "s", "start", clock.NowMs + 5000, "physical");
    scheduler.Prepare(action); scheduler.CancelAction(action.Id);
    Check(receipt is { Status: "cancelled", Simulated: false }, "Physical cancellation was mislabeled simulation");
    Check(input.Forgotten.Contains(action.Id), "Cancelled binding leaked into the next round");
});
Add("An immediate stop rejected by the input boundary produces a failed receipt", () =>
{
    var clock = new StopwatchClock(); var room = new RoomClock(clock); var input = new PhysicalBoundaryInput { Ready = false };
    using var scheduler = new ActionScheduler(clock, room, input); scheduler.ChangeEpoch("physical");
    ActionReceipt? receipt = null; scheduler.Receipt += r => receipt = r;
    Check(!scheduler.StopNow("rejected-stop", "s", "physical", "independent"), "Rejected stop accepted");
    Check(receipt is { Status: "failed", Simulated: false, ActualLocalMs: null }, "A rejected stop disappeared without a failure receipt");
    Check(receipt!.Detail == "Input boundary unavailable", "Input failure reason was lost");
});
tests.Add(("Disconnect cancels a pending start but keeps an already committed stop", async () =>
{
    var clock = new StopwatchClock(); var room = new RoomClock(clock); var input = new RecordingInput(clock);
    var t = clock.NowMs; room.AddSample(t, t, t, t);
    using var scheduler = new ActionScheduler(clock, room, input); scheduler.ChangeEpoch("a"); t = clock.NowMs; room.AddSample(t, t, t, t);
    var start = new ScheduledAction("start", "s", "start", clock.NowMs + 120, "a");
    var stop = new ScheduledAction("stop", "s", "stop", clock.NowMs + 220, "a") { Mode = "independent" };
    scheduler.Prepare(start); scheduler.Prepare(stop); scheduler.Commit(start); scheduler.Commit(stop);
    scheduler.TransportDisconnected();
    Check(input.Arrived.Wait(2000), "Committed stop was lost on tool disconnect");
    await Task.Delay(40);
    Check(input.Executed.Count == 1 && input.Executed.Single().Kind == "stop", "Disconnected start executed or stop repeated");
}));
tests.Add(("New service epoch cancels all old actions and a mismatched commit cannot change a target", async () =>
{
    var clock = new StopwatchClock(); var room = new RoomClock(clock); var input = new RecordingInput(clock);
    var t = clock.NowMs; room.AddSample(t, t, t, t);
    using var scheduler = new ActionScheduler(clock, room, input); scheduler.ChangeEpoch("old"); t = clock.NowMs; room.AddSample(t, t, t, t);
    var action = new ScheduledAction("id", "s", "start", clock.NowMs + 120, "old");
    scheduler.Prepare(action);
    Check(!scheduler.Commit(action with { TargetServerMs = action.TargetServerMs + 500 }), "Changed action accepted");
    scheduler.Commit(action); scheduler.ChangeEpoch("new");
    await Task.Delay(180); Check(input.Executed.IsEmpty, "Stale epoch emitted input");
    Check(!scheduler.Prepare(action).Ready, "Old epoch was prepared again");
}));
tests.Add(("Committed local deadline remains fixed when room clock is corrected", async () =>
{
    var local = new StopwatchClock(); var room = new RoomClock(local); var input = new RecordingInput(local);
    var t = local.NowMs; room.AddSample(t - 10, t - 5, t - 5, t);
    using var scheduler = new ActionScheduler(local, room, input); scheduler.ChangeEpoch("a"); t = local.NowMs; room.AddSample(t - 10, t - 5, t - 5, t);
    var action = new ScheduledAction("fixed", "s", "start", local.NowMs + 200, "a");
    ActionReceipt? receipt = null; scheduler.Receipt += value => receipt = value;
    scheduler.Prepare(action); scheduler.Commit(action);
    t = local.NowMs; room.AddSample(t, t + 1000, t + 1000, t);
    Check(input.Arrived.Wait(2000), "Clock correction lost scheduled input");
    await Task.Delay(20);
    Check(receipt?.PlannedLocalMs is > 0, "Receipt missing");
    Near(receipt!.PlannedLocalMs, action.TargetServerMs);
    Check(receipt.ActualLocalMs >= receipt.PlannedLocalMs - 2, "Input deadline jumped early");
}));
tests.Add(("Late start is missed while urgent stop does not require a usable clock sample", async () =>
{
    var local = new StopwatchClock(); var room = new RoomClock(local); var input = new RecordingInput(local);
    var t = local.NowMs; room.AddSample(t, t, t, t);
    using var scheduler = new ActionScheduler(local, room, input); scheduler.ChangeEpoch("a"); t = local.NowMs; room.AddSample(t, t, t, t);
    var late = new ScheduledAction("late", "s", "start", local.NowMs - 500, "a");
    Check(!scheduler.Prepare(late).Ready, "Expired start prepared");
    room.Reset();
    Check(scheduler.StopNow("urgent", "s", "a", "independent"), "Urgent stop depended on clock sync");
    Check(input.Arrived.Wait(2000), "Urgent stop not dispatched");
    await Task.Delay(10); Check(input.Executed.Single().Kind == "stop", "Wrong urgent action");
}));

tests.Add(("Duplicate immediate stop does not cancel the stop waiting behind another input", async () =>
{
    var local = new StopwatchClock(); var room = new RoomClock(local); var input = new BlockingInput(local);
    using var scheduler = new ActionScheduler(local, room, input); scheduler.ChangeEpoch("a");
    var t = local.NowMs; room.AddSample(t, t, t, t);
    var blocker = new ScheduledAction("block", "busy", "start", local.NowMs + 30, "a");
    scheduler.Prepare(blocker); scheduler.Commit(blocker);
    Check(input.Blocked.Wait(2000), "Blocking simulation did not start");
    scheduler.StopNow("urgent", "target", "a", "independent"); scheduler.StopNow("urgent", "target", "a", "independent");
    input.Release.Set();
    Check(input.Stopped.Wait(2000), "Duplicate stop cancelled the pending stop");
    await Task.Delay(10); Check(input.Stops == 1, "Stop repeated");
}));

Add("MML fixed tempo, rests, dotted lengths and ties retain the full first cycle", () =>
{
    var result = MmlTimeline.Parse("MML@T120L4c.d8r4c4&c8;", true);
    Check(result.SyntaxSupported && result.TimingSupported, "Supported score rejected");
    Near(result.FirstCycleSeconds!.Value, 2.25);
    Check(result.FullPerformanceSeconds is null && !result.GameTimingVerified, "Loop was presented as a verified finite performance");
});
Add("Global tempo changes affect all voices at the same musical beat", () =>
{
    var result = MmlTimeline.Parse("MML@T120c4T60d4,c2;", false, new(TempoScope.Global, EndScope.EntireScore));
    Near(result.Tracks[0].Seconds!.Value, 1.5); Near(result.Tracks[1].Seconds!.Value, 1.5);
    var separate = MmlTimeline.Parse("MML@T120c4T60d4,c2;", false, new(TempoScope.PerTrack, EndScope.EntireScore));
    Near(separate.Tracks[1].Seconds!.Value, 1);
});
Add("An unverified tempo scope or unknown syntax never supplies an automatic endpoint", () =>
{
    var ambiguous = MmlTimeline.Parse("MML@T150c4,c4;");
    Check(ambiguous.SyntaxSupported && !ambiguous.TimingSupported && ambiguous.FirstCycleSeconds is null, "Guessed a cross-track tempo scope");
    foreach (var mml in new[] { "MML@t120c4$;", "MML@l0c;", "MML@c4&d4;", "MML@c4&;", "MML@t0c4;", "MML@n128;" })
    {
        var invalid = MmlTimeline.Parse(mml);
        Check(!invalid.SyntaxSupported && invalid.FirstCycleSeconds is null, "Unsupported MML returned a usable endpoint");
    }
});
Add("Selected voices and whole score remain distinct until playback end semantics are chosen", () =>
{
    var ambiguous = MmlTimeline.Parse("MML@T120c4,d1;", true, new(TempoScope.Global, EndScope.Unknown, [0]));
    Near(ambiguous.SelectedTracksSeconds!.Value, .5); Near(ambiguous.WholeScoreSeconds!.Value, 2);
    Check(ambiguous.FirstCycleSeconds is null, "Assumed that a short selected part defines concert length");
    var selected = MmlTimeline.Parse("MML@T120c4,d1;", true, new(TempoScope.Global, EndScope.SelectedTracks, [0]));
    Near(selected.FirstCycleSeconds!.Value, .5);
});
Add("Absolute notes and empty trailing slots preserve track selection", () =>
{
    var result = MmlTimeline.Parse("MML@t150l8n61r8.,t150c4,,;", true, new(TempoScope.Global, EndScope.EntireScore, [0, 1]));
    Check(result.Tracks.Count == 4, "Empty slots were lost");
    Near(result.FirstCycleSeconds!.Value, .5);
    Check(!MmlTimeline.Parse("MML@c4,d4;", false, new(SelectedChannels: [2])).TimingSupported, "Nonexistent voice selected");
});
Add("Successive tempo commands at one beat use the final command and a tempo-only voice remains ambiguous", () =>
{
    var global = MmlTimeline.Parse("MML@T120T150C4,C4;", false, new(TempoScope.Global, EndScope.EntireScore));
    Check(global.TimingSupported, "Successive commands in one track were treated as a conflict");
    Near(global.FirstCycleSeconds!.Value, .4);
    Check(!MmlTimeline.Parse("MML@T150,C4;").TimingSupported, "Guessed tempo scope from a silent conducting track");
});
Add("Formatting whitespace and dotted default lengths do not change musical duration", () =>
{
    var spaced = MmlTimeline.Parse("MML@ T 120 L 8. C R ;");
    Check(spaced.TimingSupported, "Formatted score rejected"); Near(spaced.FirstCycleSeconds!.Value, .75);
});

Add("Cancellation during input preparation cannot resurrect an old start", () =>
{
    var local = new StopwatchClock(); var room = new RoomClock(local); var input = new PreparingInput();
    using var scheduler = new ActionScheduler(local, room, input); scheduler.ChangeEpoch("a");
    var t = local.NowMs; room.AddSample(t, t, t, t);
    input.OnPrepare = scheduler.CancelAll;
    var action = new ScheduledAction("race", "s", "start", local.NowMs + 5000, "a");
    Check(!scheduler.Prepare(action).Ready, "Cancelled preparation became schedulable again");
    Check(!scheduler.Commit(action), "Cancelled preparation accepted a later commit");
    Check(input.Forgotten.Contains(action.Id), "Cancelled preparation retained its input binding");
});
tests.Add(("Local configuration changes cancel starts but preserve committed music stops", async () =>
{
    var local = new StopwatchClock(); var room = new RoomClock(local); var input = new RecordingInput(local);
    using var scheduler = new ActionScheduler(local, room, input); scheduler.ChangeEpoch("a");
    var t = local.NowMs; room.AddSample(t, t, t, t);
    var start = new ScheduledAction("pending-start", "s", "start", local.NowMs + 120, "a");
    var stop = new ScheduledAction("committed-stop", "s", "stop", local.NowMs + 220, "a") { Mode = "independent" };
    scheduler.Prepare(start); scheduler.Prepare(stop); scheduler.Commit(start); scheduler.Commit(stop);
    scheduler.CancelPendingStarts();
    Check(input.Arrived.Wait(2000), "Committed stop was lost on local configuration change");
    await Task.Delay(40);
    Check(input.Executed.Count == 1 && input.Executed.Single().Kind == "stop", "Old start survived configuration change");
}));

Add("Countdown accepts fractional seconds without culture-dependent grouping", () =>
{
    Check(CountdownText.ParseMilliseconds(" 2.75 ") == 2750, "Fraction lost");
    Check(CountdownText.ParseMilliseconds("1") == 1000 && CountdownText.ParseMilliseconds("60") == 60000, "Boundary rejected");
    foreach (var text in new[] { "", "0", "60.001", "NaN", "1,000", "1e1", "2.0001" })
    {
        try { CountdownText.ParseMilliseconds(text); throw new Exception("Invalid countdown accepted: " + text); }
        catch (FormatException) { }
    }
});
Add("Combined invitations round trip without credentials or ambiguous codes", () =>
{
    var invite = new RoomInvitation(new Uri("wss://test.example/room"), "ABC234");
    Check(RoomInvitation.Parse(invite.ToString()) == invite, "Invite did not round trip");
    foreach (var text in new[] { "MabiSync/2|wss://test.example|ABC234", "MabiSync/1|https://test.example|ABC234", "MabiSync/1|ws://u:p@test.example|ABC234", "MabiSync/1|ws://test.example|ABC", "MabiSync/1|ws://test.example|ABC234|extra" })
    {
        try { RoomInvitation.Parse(text); throw new Exception("Invalid invite accepted"); }
        catch (FormatException) { }
    }
});
Add("Captured shortcuts round trip and reserved or modifier keys are rejected", () =>
{
    foreach (ushort key in new ushort[] { 0x41, 0x70, 0x0D, 0x25, 0x60, 0x2E, 0xBB })
    {
        var binding = ShortcutText.FromVirtualKey(key, true, false, true);
        Check(ShortcutText.Parse(ShortcutText.Format(binding)) == binding, "Captured key changed");
    }
    foreach (ushort key in new ushort[] { 0, 0x10, 0x11, 0x12, 0x5B, 0x78 })
    {
        try { ShortcutText.FromVirtualKey(key); throw new Exception("Invalid key accepted"); }
        catch (FormatException) { }
    }
});

tests.Add(("Manual lead freezes start and stop timing without changing the shared target", async () =>
{
    foreach (var lead in new[] { 120, -120 })
    {
        var clock = new StopwatchClock(); var room = new RoomClock(clock); var input = new RecordingInput(clock);
        using var scheduler = new ActionScheduler(clock, room, input); scheduler.ChangeEpoch("lead");
        var t = clock.NowMs; room.AddSample(t, t, t, t); scheduler.TimingOffsetMs = lead;
        var start = new ScheduledAction("start", "song", "start", clock.NowMs + 240, "lead") { Mode = "independent" };
        var stop = start with { Id = "stop", Kind = "stop", TargetServerMs = start.TargetServerMs + 140 };
        var receipts = new ConcurrentQueue<ActionReceipt>(); scheduler.Receipt += receipts.Enqueue;
        Check(scheduler.Prepare(start).Ready && scheduler.Prepare(stop).Ready, "Adjusted actions not prepared");
        scheduler.TimingOffsetMs = 0;
        Check(scheduler.Commit(start) && scheduler.Commit(stop), "Adjusted actions not committed");
        var timeout = DateTime.UtcNow.AddSeconds(3);
        while (receipts.Count < 2 && DateTime.UtcNow < timeout) await Task.Delay(10);
        var all = receipts.ToArray(); Check(all.Length == 2 && all.All(r => r.Status == "executed"), "Adjusted action failed");
        Near(all[0].PlannedLocalMs, start.TargetServerMs + lead);
        Near(all[1].PlannedLocalMs - all[0].PlannedLocalMs, 140);
        Check(all.All(r => r.AppliedTimingOffsetMs == lead), "Applied adjustment absent from receipts");
    }
}));
Add("Manual adjustment bounds are explicit and urgent stops remain immediate", () =>
{
    var clock = new StopwatchClock(); var room = new RoomClock(clock); var input = new RecordingInput(clock);
    using var scheduler = new ActionScheduler(clock, room, input); scheduler.ChangeEpoch("lead"); scheduler.TimingOffsetMs = -2000;
    ActionReceipt? receipt = null; scheduler.Receipt += r => receipt = r;
    Check(scheduler.StopNow("stop", "s", "lead", "independent"), "Urgent stop rejected");
    Check(input.Arrived.Wait(1000), "Urgent stop was delayed by manual lead");
    foreach (var invalid in new[] { -2001, 2001 }) { try { scheduler.TimingOffsetMs = invalid; throw new Exception("Invalid lead accepted"); } catch (ArgumentOutOfRangeException) { } }
});

Add("Stop rule settings preserve legacy choices and round-trip stable names", () =>
{
    Check(MusicStopSettings.Restore(null, 1) == MusicStopRule.Manual, "Legacy manual stop changed");
    Check(MusicStopSettings.Restore(null, 2) == MusicStopRule.Duration, "Legacy duration changed");
    Check(MusicStopSettings.Restore(null, 0) == MusicStopRule.Manual, "Unavailable legacy auto mode became a timer");
    foreach (var rule in new[] { MusicStopRule.Manual, MusicStopRule.Duration })
        Check(MusicStopSettings.Restore(MusicStopSettings.Persist(rule), 2) == rule, "New settings were reinterpreted as legacy indices");
    Check(MusicStopSettings.Restore("invalid", 99) == MusicStopRule.Manual, "Invalid settings armed automatic stop");
});
tests.Add(("Late score stop retains the round offset after its start has completed", async () =>
{
    var clock = new StopwatchClock(); var room = new RoomClock(clock); var input = new RecordingInput(clock);
    using var scheduler = new ActionScheduler(clock, room, input); scheduler.ChangeEpoch("late");
    var t = clock.NowMs; room.AddSample(t,t,t,t); scheduler.TimingOffsetMs = -120;
    var start = new ScheduledAction("late-start","late-song","start",clock.NowMs+180,"late") { Mode="ensemble" };
    Check(scheduler.Prepare(start).Ready && scheduler.Commit(start), "Start not prepared");
    await Task.Delay(140);
    Check(input.Executed.Any(a=>a.Id==start.Id), "Start did not execute");
    scheduler.TimingOffsetMs = 300;
    var stop = new ScheduledAction("late-stop","late-song","stop",clock.NowMs+300,"late") { Mode="ensemble" };
    Check(scheduler.Prepare(stop).Ready, "Late stop not prepared");
    Check(Math.Abs(scheduler.LocalDeadline(stop.Id)!.Value-(stop.TargetServerMs-120))<1, "Round offset changed after start completed");
    scheduler.CancelAll();
}));
Add("First-cycle stop preference roundtrips independently of legacy list indices", () =>
{
    Check(MusicStopSettings.Restore("first-cycle",null)==MusicStopRule.FirstCycle && MusicStopSettings.Persist(MusicStopRule.FirstCycle)=="first-cycle", "Cycle preference lost");
});
Add("Unified music mode keeps configured ensemble bindings or migrates the legacy independent bindings", () =>
{
    var legacy = new ModeBindings(new KeyBinding(65), new KeyBinding(66));
    var current = new ModeBindings(new KeyBinding(67), new KeyBinding(68));
    Check(MusicBindingSettings.Restore(new Dictionary<string, ModeBindings> { ["independent"] = legacy, ["ensemble"] = new() }) == legacy, "Empty ensemble slot discarded configured legacy bindings");
    Check(MusicBindingSettings.Restore(new Dictionary<string, ModeBindings> { ["independent"] = legacy, ["ensemble"] = current }) == current, "Existing ensemble binding was overwritten");
    Check(MusicBindingSettings.Restore(new Dictionary<string, ModeBindings> { ["independent"] = legacy }) == legacy, "Missing ensemble slot did not migrate");
    Check(MusicBindingSettings.Restore(new Dictionary<string, ModeBindings>()).Start is null, "Empty configuration created an input binding");
});
Add("Imported score estimates require explicit manual adoption and unsupported syntax has no duration", () =>
{
    var score = ImportedMml.Parse("short", "MML@T120L4cr;", true);
    Check(score.SuggestedManualMilliseconds == 1000 && !score.AutomaticEndAvailable, "Import authorized a verified game endpoint");
    var unsupported = ImportedMml.Parse("unknown", "MML@T120c$;", false);
    Check(unsupported.SuggestedManualMilliseconds is null && !unsupported.AutomaticEndAvailable, "Unknown syntax supplied a duration");
    var ambiguous = ImportedMml.Parse("multi", "MML@T150c4,c4;", false);
    Check(ambiguous.SuggestedManualMilliseconds is null, "Import guessed cross-track timing");
});
Add("Manual music duration keeps millisecond precision and rejects ambiguous or invalid input", () =>
{
    Check(MusicDurationText.ParseMilliseconds(" 1.275 ") == 1275 && MusicDurationText.ParseMilliseconds("86400") == 86400000, "Manual duration changed");
    foreach (var value in new[] { "0", "-1", "NaN", "1,000", "86400.001", "0.0001" })
    { try { MusicDurationText.ParseMilliseconds(value); throw new Exception("Invalid duration accepted"); } catch (FormatException) { } }
});
Add("Music interruption controls distinguish emotes, leader reports and host decisions", () =>
{
    Check(!MusicRoundActions.For("emote", "performing", true, true, true).CanStopAll, "Emote exposed a music stop");
    var leader = MusicRoundActions.For("independent", "performing", false, true, true);
    Check(leader.CanReportInterruption && !leader.CanContinue && !leader.CanStopAll, "Leader acquired host rights");
    var host = MusicRoundActions.For("ensemble", "interrupted", true, false, true);
    Check(host.CanContinue && host.CanStopAll && !host.CanReportInterruption, "Host cannot decide interruption");
    Check(!MusicRoundActions.For("independent", "committed", false, true, false).CanReportInterruption, "Report enabled before scheduled start");
    Check(!MusicRoundActions.For("independent", "stopped", true, true, true).CanStopAll, "A completed round retained stop controls");
});

var failed = 0;
Add("Explicit room exit cancels future stops while allowing its immediate stop to finish",()=>{
    var method=typeof(ActionScheduler).GetMethod("RoomExited");Check(method is not null,"Explicit exit still reuses temporary disconnect behavior");
    var local=new StopwatchClock();var room=new RoomClock(local);var input=new BlockingInput(local);
    using var scheduler=new ActionScheduler(local,room,input);scheduler.ChangeEpoch("exit");var t=local.NowMs;room.AddSample(t,t,t,t);
    var future=new ScheduledAction("future-stop","old","stop",local.NowMs+5000,"exit"){Mode="ensemble"};
    Check(scheduler.Prepare(future).Ready && scheduler.Commit(future),"Future stop not armed");
    method!.Invoke(scheduler,null);Check(scheduler.LocalDeadline(future.Id) is null,"Room exit retained a future click");
    scheduler.StopNow("block","old","exit","ensemble");Check(input.Blocked.Wait(1000),"Immediate stop not executing");var releases=input.Releases;
    try {method.Invoke(scheduler,null);Check(input.Releases==releases,"Room exit truncated its immediate stop");}
    finally {input.Release.Set();}
});
Add("Cancelling unrelated work or disconnecting never truncates an executing committed stop", () =>
{
    var local = new StopwatchClock(); var room = new RoomClock(local); var input = new BlockingInput(local);
    using var scheduler = new ActionScheduler(local, room, input); scheduler.ChangeEpoch("release");
    var t = local.NowMs; room.AddSample(t,t,t,t);
    var stop = new ScheduledAction("block","music","stop",local.NowMs+10,"release") { Mode="ensemble" };
    Check(scheduler.Prepare(stop).Ready && scheduler.Commit(stop), "Stop not armed");
    Check(input.Blocked.Wait(1000), "Stop did not begin");
    var releases=input.Releases;
    try
    {
        scheduler.CancelAction("old-unrelated-action");scheduler.TransportDisconnected();scheduler.CancelPendingStarts();
        Check(input.Releases==releases,"An unrelated cancellation released the committed stop");
        scheduler.CancelAction(stop.Id);
        Check(input.Releases>releases,"Cancelling the executing action did not release it");
    }
    finally { input.Release.Set(); }
});
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed");
return failed == 0 ? 0 : 1;

sealed class ManualClock : IMonotonicClock { public double NowMs { get; set; } }
sealed class PreparingInput : IInputExecutor
{
    public Action? OnPrepare; public HashSet<string> Forgotten = [];
    public InputPreparation Prepare(ScheduledAction action) { OnPrepare?.Invoke(); return new(true, null); }
    public InputOutcome Execute(ScheduledAction action, CancellationToken token) => throw new InvalidOperationException("No input expected");
    public void Forget(string id) => Forgotten.Add(id);
    public void ReleaseAll() { }
}
sealed class PhysicalBoundaryInput : IInputExecutor
{
    public bool Ready = true;
    public HashSet<string> Forgotten = [];
    public bool IsSimulation(ScheduledAction action) => false;
    public InputPreparation Prepare(ScheduledAction action) => new(Ready, Ready ? null : "Input boundary unavailable");
    public InputOutcome Execute(ScheduledAction action, CancellationToken token) => throw new InvalidOperationException("No OS input in this test");
    public void Forget(string actionId) => Forgotten.Add(actionId);
    public void ReleaseAll() { }
}
sealed class RecordingInput(IMonotonicClock clock) : IInputExecutor
{
    public ConcurrentQueue<ScheduledAction> Executed { get; } = new();
    public ManualResetEventSlim Arrived { get; } = new();
    public InputPreparation Prepare(ScheduledAction action) => new(true, null);
    public InputOutcome Execute(ScheduledAction action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Executed.Enqueue(action); Arrived.Set();
        return new("executed", clock.NowMs, true, "Simulated input only");
    }
    public void ReleaseAll() { }
}
sealed class BlockingInput(IMonotonicClock clock) : IInputExecutor
{
    public ManualResetEventSlim Blocked { get; } = new();
    public ManualResetEventSlim Release { get; } = new();
    public ManualResetEventSlim Stopped { get; } = new();
    public int Stops;
    public int Releases;
    public InputPreparation Prepare(ScheduledAction action) => new(true, null);
    public InputOutcome Execute(ScheduledAction action, CancellationToken token)
    {
        if (action.Id == "block") { Blocked.Set(); Release.Wait(1500, token); }
        else { Interlocked.Increment(ref Stops); Stopped.Set(); }
        return new("executed", clock.NowMs, true, "Simulation");
    }
    public void ReleaseAll() => Interlocked.Increment(ref Releases);
}
