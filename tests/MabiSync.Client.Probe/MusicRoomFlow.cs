using System.Collections.Concurrent;
using System.Text.Json;
using MabiSync.Client;
using MabiSync.Core;

// Room integration only. All window inspection and key events use a fake OS.
internal static class MusicRoomFlow
{
    public static async Task<int> Run(string endpoint)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var host = new Peer(-80); await using var member = new Peer(80);
        var stage = "connect";
        try
        {
            await host.Client.ConnectAsync(endpoint, limit.Token); await member.Client.ConnectAsync(endpoint, limit.Token);
            var identity = await host.Client.CreateRoomAsync("Music Host", limit.Token);
            var joined = await member.Client.JoinRoomAsync("Music Leader", identity.Code, limit.Token);
            var ids = new[] { identity.MemberId, joined.MemberId };
            await host.Client.SendAsync(new { type = "select-leader", memberId = joined.MemberId });
            await Wait(() => host.Client.Room?.GetProperty("leaderId").GetString() == joined.MemberId && host.RoomClock.Estimate is not null && member.RoomClock.Estimate is not null);
            async Task Ready(string mode)
            {
                await host.Ready(mode); await member.Ready(mode, joiner: mode == "ensemble");
                await Wait(() => host.Client.Room!.Value.GetProperty("members").EnumerateArray().All(m => m.GetProperty("ready").GetBoolean()));
            }
            async Task Start(string mode, int? duration)
            {
                var old = Performance()?.GetProperty("id").GetString();
                await host.Client.SendAsync(new { type = "prepare", mode, participantIds = ids, countdownMs = 1000, stopAfterMs = duration });
                await Wait(() => Performance()?.GetProperty("id").GetString() != old && Phase("committed"));
            }
            stage = "timed independent";
            await Ready("independent"); await Start("independent", 350);
            await Wait(() => Phase("stopped") && host.Input.Stops == 1 && member.Input.Stops == 1 && Performance()?.GetProperty("receipts").GetArrayLength() == 4);
            Check(host.Input.Starts == 1 && member.Input.Starts == 1, "Timed music started twice");
            Check(host.Client.Room!.Value.GetProperty("members").EnumerateArray().All(m => !m.GetProperty("ready").GetBoolean()), "Music did not consume readiness");

            stage = "ensemble interruption continue and stop";
            await Ready("ensemble"); await Start("ensemble", null);
            await Wait(() => host.Input.Starts == 2 && Phase("performing"));
            Check(member.Input.Starts == 1, "Manual ensemble joiner received a start input");
            var session = Performance()!.Value.GetProperty("id").GetString();
            await member.Client.SendAsync(new { type = "leader-status", sessionId = session, status = "interrupted", evidenceSource = "player-report" });
            await Wait(() => Phase("interrupted"));
            Check(Performance()!.Value.GetProperty("interruption").GetProperty("evidenceSource").GetString() == "player-report", "Player report mislabeled");
            await host.Client.SendAsync(new { type = "decide", decision = "continue" });
            await Wait(() => Phase("performing"));
            Check(host.Input.Stops == 1 && member.Input.Stops == 1, "Continue unexpectedly sent input");
            await member.Client.SendAsync(new { type = "leader-status", sessionId = session, status = "interrupted", evidenceSource = "player-report" });
            await Wait(() => Phase("interrupted"));
            await host.Client.SendAsync(new { type = "decide", decision = "stop" });
            await Wait(() => Phase("stopped") && host.Input.Stops == 2 && member.Input.Stops == 2);
            await host.Client.SendAsync(new { type = "stop-all" });
            await Task.Delay(80, limit.Token);
            Check(host.Input.Stops == 2 && member.Input.Stops == 2, "Duplicate stop repeated game input");

            stage = "committed stop after disconnect";
            await Ready("independent"); await Start("independent", 700);
            await Wait(() => host.Input.Starts == 3 && member.Input.Starts == 2);
            await member.Client.DisconnectAsync();
            await Wait(() => Phase("interrupted"));
            await host.Client.SendAsync(new { type = "decide", decision = "continue" });
            await Wait(() => Phase("stopped") && host.Input.Stops == 3 && member.Input.Stops == 3);
            await member.Client.ConnectAsync(endpoint, limit.Token);
            await Wait(() => member.Client.Connected && member.RoomClock.Estimate is not null);
            await Task.Delay(80, limit.Token);
            Check(member.Input.Starts == 2 && member.Input.Stops == 3, "Resume replayed music actions");

            stage = "cancel next round";
            await Ready("independent"); await Start("independent", 350);
            await host.Client.SendAsync(new { type = "cancel" });
            await Wait(() => Phase("cancelled"));
            await Task.Delay(1500, limit.Token);
            Check(host.Input.Starts == 3 && member.Input.Starts == 2 && host.Input.Stops == 3 && member.Input.Stops == 3, "Cancelled music emitted input");
            Check(host.Notices.IsEmpty && member.Notices.IsEmpty, "Unexpected notice: " + string.Join(",", host.Notices.Concat(member.Notices)));
            Check(host.Input.BadKeys == 0 && member.Input.BadKeys == 0, "Wrong binding dispatched");
            stage = "captured first-cycle stop prepared after start and retained offline";
            await Ready("independent");
            var before = Performance()?.GetProperty("id").GetString();
            await host.Client.SendAsync(new {type="prepare",mode="independent",participantIds=ids,countdownMs=1000,stopRule="first-cycle"});
            await Wait(()=>Performance()?.GetProperty("id").GetString()!=before && Phase("committed"));
            await Wait(()=>host.Input.Starts==4 && member.Input.Starts==3 && Phase("performing"));
            var song=Performance()!.Value;
            await member.Client.SendAsync(new {type="game-observation",sessionId=song.GetProperty("id").GetString(),generation="fixture-cycle",sequence=1,status="identified",
                identity=new {characterId="101",realmId=3,nickname="Music Leader"},music=new {concertId="7",performerCharacterId="101",hostCharacterId="101",title="Fixture",isLoop=true,
                selectedChannels=new[]{0},scoreSha256=new string('a',64),wholeScoreDurationSeconds=1.2,selectedDurationSeconds=1.2,
                timingSource="tw-receive-offset-v1",estimatedStartServerMs=member.RoomClock.ServerNowMs}});
            await Wait(()=>Performance() is { } score && score.TryGetProperty("stopCommitted",out var committed) && committed.GetBoolean());
            Check(Performance()!.Value.GetProperty("stopTimingSource").GetString()=="leader-capture-estimate","Cycle source mislabeled");
            await member.Client.DisconnectAsync();
            await Wait(()=>Phase("stopped") && host.Input.Stops==4 && member.Input.Stops==4);
            Check(host.Notices.IsEmpty && member.Notices.IsEmpty,"Unexpected cycle notice");
            stage = "manual ensemble leader supplies a first-cycle stop for all members";
            await member.Client.ConnectAsync(endpoint, limit.Token);
            await Wait(()=>member.Client.Connected && member.RoomClock.Estimate is not null);
            await Ready("ensemble");
            before=Performance()?.GetProperty("id").GetString();
            await host.Client.SendAsync(new {type="prepare",mode="ensemble",participantIds=ids,countdownMs=1000,stopRule="first-cycle"});
            await Wait(()=>Performance()?.GetProperty("id").GetString()!=before && Phase("committed"));
            await Wait(()=>host.Input.Starts==5 && Phase("performing"));
            Check(member.Input.Starts==3,"Manual joiner was automatically started");
            await member.Client.SendAsync(new {type="game-observation",sessionId=Performance()!.Value.GetProperty("id").GetString(),generation="fixture-ensemble",sequence=2,status="identified",
                identity=new {characterId="101",realmId=3,nickname="Music Leader"},music=new {concertId="8",performerCharacterId="101",hostCharacterId="102",title="Fixture ensemble",isLoop=true,
                selectedChannels=new[]{1},scoreSha256=new string('b',64),wholeScoreDurationSeconds=1.2,selectedDurationSeconds=1.0,
                timingSource="tw-receive-offset-v1",estimatedStartServerMs=member.RoomClock.ServerNowMs}});
            await Wait(()=>Phase("stopped") && host.Input.Stops==5 && member.Input.Stops==5);
            Check(member.Input.Starts==3 && host.Notices.IsEmpty && member.Notices.IsEmpty,"Ensemble cycle repeated a start or raised an error");
            Console.WriteLine("{\"status\":\"passed\",\"completedMusicRounds\":5,\"cancelledRounds\":1,\"desktopInputs\":0,\"gameResponseVerified\":false}"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(stage + ": " + error + "\nNotices: " + string.Join(";",host.Notices.Concat(member.Notices)) + "\nState: " + host.Client.Room); return 1; }
        async Task Wait(Func<bool> test) { while (!test()) await Task.Delay(10, limit.Token); }
        JsonElement? Performance() => host.Client.Room?.GetProperty("performance") is { ValueKind: JsonValueKind.Object } p ? p : null;
        bool Phase(string phase) => Performance()?.GetProperty("phase").GetString() == phase;
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private sealed class Peer : IAsyncDisposable
    {
        private readonly StopwatchClock _clock = new();
        private readonly ActionScheduler _scheduler;
        private readonly InputRouter _router;
        public readonly RoomClock RoomClock;
        public readonly RoomClient Client;
        public readonly FakePlatform Input = new();
        public readonly ConcurrentQueue<string> Notices = new();
        public Peer(int offset)
        {
            RoomClock = new(_clock);
            var binding = new ModeBindings(new KeyBinding(65), new KeyBinding(66));
            var profile = new GameInputProfile(Input.Window.Identity, new(), binding, binding);
            var game = new GameInputExecutor(_clock, Input, () => profile, new UnknownPlaybackEvidence());
            _router = new(new SimulationInput(_clock), game) { UseSimulation = false, StartsEnabled = false };
            _scheduler = new(_clock, RoomClock, _router) { TimingOffsetMs = offset }; Client = new(_clock, RoomClock, _scheduler);
            Client.Notice += Notices.Enqueue;
        }
        public Task Ready(string mode, bool joiner = false)
        {
            _router.ReadyMode = mode; _router.StartsEnabled = !joiner;
            return Client.SendAsync(new { type = "set-ready", ready = true, capabilities = new { simulation = false, canStart = !joiner, canStop = true, musicRole = joiner ? "joiner" : "initiator", timingOffsetMs = _scheduler.TimingOffsetMs } });
        }
        public async ValueTask DisposeAsync() { await Client.DisposeAsync(); _scheduler.Dispose(); }
    }
    private sealed class FakePlatform : IGameInputPlatform
    {
        public readonly GameWindowState Window = new(new(123, 456, 1, "C:/Fake/MabinogiMobile.exe"), new(0, 0, 1280, 960, 96), true, true, true);
        public int Starts, Stops, BadKeys;
        public GameWindowState Inspect(GameWindowId window) => Window;
        public Task ActivateAsync(GameWindowId window, CancellationToken token=default) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public bool KeyIsDown(ushort key) => false;
        public bool LeftButtonIsDown() => false;
        public bool PointBelongsToWindow(GameWindowId window, int x, int y) => false;
        public void Key(ushort key, bool down) { if (!down) return; if (key == 65) Interlocked.Increment(ref Starts); else if (key == 66) Interlocked.Increment(ref Stops); else Interlocked.Increment(ref BadKeys); }
        public void MovePointer(int x, int y) => Interlocked.Increment(ref BadKeys);
        public void LeftButton(bool down) => Interlocked.Increment(ref BadKeys);
    }
}
