using System.Text.Json;
using MabiSync.Client;
using MabiSync.Core;

internal static class RoomSetupFlow
{
    public static async Task<int> Run(string endpoint)
    {
        var clock = new StopwatchClock(); var hostClock = new RoomClock(clock); var guestClock = new RoomClock(clock);
        using var hs = new ActionScheduler(clock, hostClock, new SimulationInput(clock));
        using var gs = new ActionScheduler(clock, guestClock, new SimulationInput(clock));
        await using var host = new RoomClient(clock, hostClock, hs); await using var guest = new RoomClient(clock, guestClock, gs);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await host.ConnectAsync(endpoint, limit.Token); await guest.ConnectAsync(endpoint, limit.Token);
            try { await guest.JoinRoomAsync("Missing", "000000", limit.Token); throw new Exception("Bad room accepted"); }
            catch (InvalidOperationException error) when (error.Message == "ROOM_NOT_FOUND") { }
            var session = await host.CreateRoomAsync("Host", limit.Token);
            var invitation = RoomInvitation.Parse(new RoomInvitation(new Uri(endpoint), session.Code).ToString());
            var joined = await guest.JoinRoomAsync("Guest", invitation.Code, limit.Token);
            if (joined.Code != session.Code) throw new Exception("Invitation joined wrong room");
            await Wait(() => host.Room?.GetProperty("members").GetArrayLength() == 2 && guestClock.Estimate is not null && hostClock.Estimate is not null);
            await host.SendAsync(new { type = "set-ready", ready = true, capabilities = new { simulation = true, canStart = true, canStop = false } });
            await guest.SendAsync(new { type = "set-ready", ready = true, capabilities = new { simulation = true, canStart = true, canStop = false } });
            await Wait(() => host.Room!.Value.GetProperty("members").EnumerateArray().All(m => m.GetProperty("ready").GetBoolean()));
            await host.SendAsync(new { type = "prepare", mode = "emote", participantIds = new[] { session.MemberId, joined.MemberId }, countdownMs = CountdownText.ParseMilliseconds("2.75") });
            await Wait(() => guest.Room?.GetProperty("performance") is { ValueKind: JsonValueKind.Object } p && p.GetProperty("phase").GetString() == "committed");
            if (guest.Room!.Value.GetProperty("performance").GetProperty("countdownMs").GetInt32() != 2750) throw new Exception("Custom countdown not shared");
            await host.SendAsync(new { type = "cancel" });
            await Wait(() => guest.Room!.Value.GetProperty("performance").GetProperty("phase").GetString() == "cancelled");
            try { guest.ForgetDisconnectedRoom(); throw new Exception("Forgot a live room"); }
            catch (InvalidOperationException error) when (error.Message == "请先断开连接，再清除本机房间身份。") { }
            await guest.DisconnectAsync(); guest.ForgetDisconnectedRoom();
            if (guest.Session is not null || guest.Room is not null) throw new Exception("Disconnected room trapped the UI");
            await guest.ConnectAsync(endpoint, limit.Token);
            await guest.JoinRoomAsync("Guest rejoined", invitation.Code, limit.Token);
            Console.WriteLine("{\"status\":\"passed\",\"invitation\":true,\"countdownMs\":2750,\"desktopInputs\":0}"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        async Task Wait(Func<bool> test) { while (!test()) await Task.Delay(10, limit.Token); }
    }
}
