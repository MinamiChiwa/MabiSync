using System.Text.Json;
using MabiSync.Core;
using MabiSync.Client;

if (args.Length is < 1 or > 2) return 2;
if(args.Length==2 && args[1].StartsWith("resume-"))return await ResumeRoomFlow.Run(args[0],args[1]);
if (args.Length == 2 && args[1] == "music-flow") return await MusicRoomFlow.Run(args[0]);
if (args.Length == 2 && args[1] == "room-setup") return await RoomSetupFlow.Run(args[0]);
if (args.Length == 2 && args[1] == "emote-flow") return await EmoteRoomFlow.Run(args[0]);
if(args.Length==2 && args[1]=="timing-flow")return await TimingRoomFlow.Run(args[0]);
if(args.Length==2 && args[1]=="focus-flow")return await FocusRoomFlow.Run(args[0]);
if(args.Length==2 && args[1]=="silent-room") {
    var local=new StopwatchClock();var roomTime=new RoomClock(local);var simulated=new SimulationInput(local);
    using var schedule=new ActionScheduler(local,roomTime,simulated);
    var constructor=typeof(RoomClient).GetConstructors().Single();
    if(constructor.GetParameters().Length!=4){Console.WriteLine("Missing silent connection detection");return 1;}
    await using var peer=(RoomClient)constructor.Invoke([local,roomTime,schedule,TimeSpan.FromMilliseconds(250)]);
    await peer.ConnectAsync(args[0]);await peer.CreateRoomAsync("Silent fixture");
    using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(3));
    while(peer.Connected)await Task.Delay(10,limit.Token);
    if(simulated.Count!=0){Console.WriteLine("Connection loss dispatched an input");return 1;}
    Console.WriteLine("PASS silent public connection marked disconnected with zero inputs");return 0;
}
if (args.Length == 2 && args[1] == "probe-tunnel")
{
    var probe = new QuickTunnelProbe(); var uri = new Uri(args[0]);
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var epoch = await probe.LocalEpochAsync(uri.Port, limit.Token);
    if (!await probe.VerifyAsync(uri, epoch, limit.Token) || await probe.VerifyAsync(uri, Guid.NewGuid().ToString(), limit.Token)) return 1;
    Console.WriteLine("{\"status\":\"passed\",\"httpAndWebSocketVerified\":true,\"wrongEpochRejected\":true}"); return 0;
}
var cancelled = args.Length == 2 && args[1] == "cancel";
var urgent = args.Length == 2 && args[1] == "stopnow";
var localStop = args.Length == 2 && args[1] == "localstop";
var resetIdentity = args.Length == 2 && args[1] is "reset-identity" or "switch-endpoint";
var clock = new StopwatchClock();
var roomClock = new RoomClock(clock);
var input = new SimulationInput(clock);
using var scheduler = new ActionScheduler(clock, roomClock, input);
await using var client = new RoomClient(clock, roomClock, scheduler);
var notices = new System.Collections.Concurrent.ConcurrentQueue<string>();
client.Notice += notices.Enqueue;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
try
{
    await client.ConnectAsync(args[0], timeout.Token);
    await WaitUntil(() => roomClock.Estimate is not null);
    await client.SendAsync(new { type = "create", nickname = "Native simulation" });
    await WaitUntil(() => client.Session is not null);
    if (resetIdentity)
    {
        await WaitUntil(() => client.Room is not null);
        var nextEndpoint = args[1] == "switch-endpoint" ? args[0].TrimEnd('/') + "/other" : args[0];
        await client.ConnectAsync(nextEndpoint, timeout.Token);
        if (client.Session is not null || client.Room is not null) throw new InvalidOperationException("New service epoch retained an old room snapshot");
        if (input.Count != 0) throw new InvalidOperationException("Reconnect emitted an input");
        Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", inputCount = input.Count, oldRoomCleared = true }));
        return 0;
    }
    var id = client.Session!.MemberId;
    await client.SendAsync(new { type = "set-ready", ready = true, capabilities = new { simulation = true, canStart = true, canStop = true } });
    await WaitUntil(() => client.Room is { } room && room.GetProperty("members").EnumerateArray().Any(m => m.GetProperty("id").GetString() == id && m.GetProperty("ready").GetBoolean()));
    if (urgent || localStop)
    {
        await client.SendAsync(new { type = "prepare", mode = "independent", participantIds = new[] { id }, countdownMs = 1000 });
        await WaitUntil(() => client.Room is { } room && room.GetProperty("performance").ValueKind == JsonValueKind.Object && room.GetProperty("performance").GetProperty("receipts").GetArrayLength() == 1);
        if (localStop)
        {
            var received = new TaskCompletionSource<ActionReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.ReceiptReported += r => { if (!r.ReportToRoom) received.TrySetResult(r); };
            var session = client.Room!.Value.GetProperty("performance").GetProperty("id").GetString()!;
            if (!scheduler.StopNow("private-local-stop", session, client.Session!.ServerEpoch, "independent", localOnly: true)) throw new InvalidOperationException("Local stop was not prepared");
            var receipt = await received.Task.WaitAsync(timeout.Token);
            await Task.Delay(200, timeout.Token);
            var count = client.Room!.Value.GetProperty("performance").GetProperty("receipts").GetArrayLength();
            if (receipt.Status != "executed" || count != 1 || input.Count != 2 || !notices.IsEmpty) throw new InvalidOperationException("Private stop leaked into room receipts or failed locally: " + string.Join(",", notices));
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", inputCount = input.Count, receipts = count, localStopReported = true, simulated = true, gameResponseVerified = false }));
            return 0;
        }
        await client.SendAsync(new { type = "stop-all" });
    }
    else await client.SendAsync(new { type = "prepare", mode = "independent", participantIds = new[] { id }, countdownMs = 1000, stopAfterMs = 200 });
    await WaitUntil(() => client.Room is { } room && room.GetProperty("performance").ValueKind == JsonValueKind.Object && room.GetProperty("performance").GetProperty("receipts").GetArrayLength() == 2);
    var receipts = client.Room!.Value.GetProperty("performance").GetProperty("receipts").EnumerateArray().ToArray();
    if (receipts.Any(r => r.GetProperty("status").GetString() != (cancelled ? "cancelled" : "executed") || !r.GetProperty("simulated").GetBoolean() || r.GetProperty("gameResponseVerified").GetBoolean())) throw new InvalidOperationException("Invalid simulation receipts");
    if (input.Count != (cancelled ? 0 : 2)) throw new InvalidOperationException("Unexpected input count");
    if (!notices.IsEmpty) throw new InvalidOperationException("Unexpected service notice: " + string.Join(", ", notices));
    Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", inputCount = input.Count, receipts = receipts.Length, simulated = true, gameResponseVerified = false }));
    return 0;
}
catch (Exception error) { Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", error = error.Message })); return 1; }

async Task WaitUntil(Func<bool> predicate)
{
    while (!predicate()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(10, timeout.Token); }
}
