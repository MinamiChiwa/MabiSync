using System.Collections.Concurrent;
using System.Text.Json;
using MabiSync.Client;
using MabiSync.Core;

// Real native clients + real room service; fake OS boundary, never desktop input.
internal static class EmoteRoomFlow
{
    public static async Task<int> Run(string endpoint)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var host = new Peer(); await using var member = new Peer();
        async Task Wait(Func<bool> predicate)
        { while (!predicate()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(10, timeout.Token); } }
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static JsonElement? Performance(Peer peer) => peer.Client.Room?.GetProperty("performance") is { ValueKind: JsonValueKind.Object } p ? p : null;
        static bool Phase(Peer peer, string phase) => Performance(peer)?.GetProperty("phase").GetString() == phase;
        static bool AllUnready(Peer peer) => peer.Client.Room?.GetProperty("members").EnumerateArray().All(m => !m.GetProperty("ready").GetBoolean()) == true;
        try
        {
            await host.Client.ConnectAsync(endpoint, timeout.Token); await member.Client.ConnectAsync(endpoint, timeout.Token);
            await Wait(() => host.RoomClock.Estimate is not null && member.RoomClock.Estimate is not null);
            await host.Client.SendAsync(new { type = "create", nickname = "Fake OS Host" });
            await Wait(() => host.Client.Session is not null);
            await member.Client.SendAsync(new { type = "join", code = host.Client.Session!.Code, nickname = "Fake OS Member" });
            await Wait(() => member.Client.Session is not null && host.Client.Room?.GetProperty("members").GetArrayLength() == 2);
            var ids = new[] { host.Client.Session!.MemberId, member.Client.Session!.MemberId };
            async Task ReadyBoth()
            {
                await host.Ready(); await member.Ready();
                await Wait(() => host.Client.Room?.GetProperty("members").EnumerateArray().All(m => m.GetProperty("ready").GetBoolean()) == true);
            }
            async Task<string> Start()
            {
                var old = Performance(host)?.GetProperty("id").GetString();
                await host.Client.SendAsync(new { type = "prepare", mode = "emote", participantIds = ids, countdownMs = 1000 });
                await Wait(() => Performance(host)?.GetProperty("id").GetString() != old && Phase(host, "committed"));
                return Performance(host)!.Value.GetProperty("id").GetString()!;
            }
            async Task Complete(int expected)
            {
                await Wait(() => Phase(host, "stopped") && Performance(host)?.GetProperty("receipts").GetArrayLength() == 2 && AllUnready(host) && AllUnready(member));
                Check(host.Platform.EnterDowns == expected && member.Platform.EnterDowns == expected, "Emote Enter count differed between peers");
                Check(host.Platform.EnterUps == expected && member.Platform.EnterUps == expected, "Enter was not released");
                foreach (var receipt in Performance(host)!.Value.GetProperty("receipts").EnumerateArray())
                    Check(receipt.GetProperty("status").GetString() == "executed" && !receipt.GetProperty("simulated").GetBoolean() && !receipt.GetProperty("gameResponseVerified").GetBoolean(), "Input route or game response evidence was mislabeled");
            }
            await ReadyBoth(); var first = await Start(); await Complete(1);
            await host.Client.SendAsync(new { type = "prepare", mode = "emote", participantIds = ids, countdownMs = 1000 });
            await Wait(() => host.Notices.TryPeek(out _));
            Check(host.Notices.TryDequeue(out var notice) && notice == "MEMBER_NOT_READY", "An old draft was accepted for a new round");
            Check(Performance(host)?.GetProperty("id").GetString() == first, "Unready round replaced the completed session");
            await ReadyBoth(); var second = await Start(); await Complete(2);
            Check(first != second, "Two rounds reused a session identity");

            await ReadyBoth(); var cancelled = await Start();
            await member.Cancel();
            await Wait(() => Phase(host, "cancelled") && Performance(host)?.GetProperty("receipts").GetArrayLength() == 2 && AllUnready(host));
            Check(host.Platform.EnterDowns == 2 && member.Platform.EnterDowns == 2, "Cancelled trigger emitted Enter");

            await ReadyBoth(); var disconnected = await Start();
            await member.Client.DisconnectAsync();
            await Wait(() => Phase(host, "cancelled") && !host.Router.StartsEnabled);
            await member.Client.ConnectAsync(endpoint, timeout.Token);
            await Wait(() => member.Client.Connected && member.RoomClock.Estimate is not null);
            // Wait past the old target: reconnect must never replay the cancelled start.
            await Task.Delay(1200, timeout.Token);
            Check(host.Platform.EnterDowns == 2 && member.Platform.EnterDowns == 2, "A disconnected round replayed input after resume");
            host.SetTimingOffset(-120); member.SetTimingOffset(120);
            await ReadyBoth(); var resumed = await Start(); await Complete(3);
            Check(resumed != disconnected && resumed != cancelled, "Resume reused a cancelled session");
            Check(host.Notices.IsEmpty && member.Notices.IsEmpty, "Unexpected notice: " + string.Join(",", host.Notices.Concat(member.Notices)));
            Check(host.Platform.OtherInputs == 0 && member.Platform.OtherInputs == 0, "Emote flow sent a stop, text, or pointer input");
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", peerCount = 2, completedRounds = 3, cancelledRounds = 2, enterDownsPerPeer = 3, osInputFaked = true, desktopInputs = 0, physicalTwoPcSyncVerified = false }));
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", error = error.Message, hostNotices = host.Notices.ToArray(), memberNotices = member.Notices.ToArray(), hostPhase = Performance(host)?.GetProperty("phase").GetString(), memberPhase = Performance(member)?.GetProperty("phase").GetString(), hostKeys = host.Platform.EnterDowns, memberKeys = member.Platform.EnterDowns, osInputFaked = true }));
            return 1;
        }
    }
    private sealed class Peer : IAsyncDisposable
    {
        private readonly StopwatchClock _clock = new();
        private readonly GameInputExecutor _game;
        private readonly ActionScheduler _scheduler;
        public readonly RoomClock RoomClock;
        public readonly FakePlatform Platform = new();
        public readonly InputRouter Router;
        public readonly RoomClient Client;
        public readonly ConcurrentQueue<string> Notices = new();
        public Peer()
        {
            RoomClock = new(_clock);
            var profile = new GameInputProfile(Platform.Window.Identity, new(new ChatDraftBinding()), new(), new());
            _game = new(_clock, Platform, () => profile, new UnknownPlaybackEvidence());
            Router = new(new SimulationInput(_clock), _game) { UseSimulation = false, StartsEnabled = false, ReadyMode = "emote" };
            _scheduler = new(_clock, RoomClock, Router); Client = new(_clock, RoomClock, _scheduler);
            Client.Notice += Notices.Enqueue;
            Client.ReceiptReported += r => { if (!r.Simulated) { Router.StartsEnabled = false; _game.RevokeChatDraft(); _ = ClearReady(); } };
        }
        public Task Ready()
        {
            _game.ConfirmChatDraft(); Router.ReadyMode = "emote"; Router.StartsEnabled = true;
            return Client.SendAsync(new { type = "set-ready", ready = true, capabilities = new { simulation = false, canStart = true, canStop = false, timingOffsetMs = _scheduler.TimingOffsetMs } });
        }
        public void SetTimingOffset(int value) => _scheduler.TimingOffsetMs = value;
        private async Task ClearReady()
        {
            try { if (Client.Connected) await Client.SendAsync(new { type = "set-ready", ready = false, capabilities = new { simulation = false, canStart = false, canStop = false } }); }
            catch (Exception error) { Notices.Enqueue(error.Message); }
        }
        public Task Cancel()
        {
            Router.StartsEnabled = false; _game.RevokeChatDraft(); _scheduler.CancelPendingStarts();
            return Client.SendAsync(new { type = "local-stop" });
        }
        public async ValueTask DisposeAsync() { await Client.DisposeAsync(); _scheduler.Dispose(); }
    }
    private sealed class FakePlatform : IGameInputPlatform
    {
        public readonly GameWindowState Window = new(new(123, 456, 1, "C:/Fake/MabinogiMobile.exe"), new(0, 0, 1280, 960, 96), true, true, true);
        public int EnterDowns, EnterUps, OtherInputs;
        public GameWindowState Inspect(GameWindowId window) => Window;
        public Task ActivateAsync(GameWindowId window, CancellationToken token=default) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public bool KeyIsDown(ushort key) => false;
        public bool LeftButtonIsDown() => false;
        public bool PointBelongsToWindow(GameWindowId window, int x, int y) => false;
        public void Key(ushort key, bool down) { if (key != 13) { Interlocked.Increment(ref OtherInputs); return; } if (down) Interlocked.Increment(ref EnterDowns); else Interlocked.Increment(ref EnterUps); }
        public void MovePointer(int x, int y) => Interlocked.Increment(ref OtherInputs);
        public void LeftButton(bool down) => Interlocked.Increment(ref OtherInputs);
    }
}
