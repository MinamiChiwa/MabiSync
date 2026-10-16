using System.Net.WebSockets;
using MabiSync.Client;
using MabiSync.Core;

static class ResumeRoomFlow
{
    public static async Task<int> Run(string endpoint,string scenario)
    {
        var clock=new StopwatchClock();var room=new RoomClock(clock);var input=new SimulationInput(clock);
        using var scheduler=new ActionScheduler(clock,room,input);
        await using var client=new RoomClient(clock,room,scheduler);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(6));
        await client.ConnectAsync(endpoint,timeout.Token);await client.CreateRoomAsync("Resume fixture",timeout.Token);
        while(client.Room is null)await Task.Delay(10,timeout.Token);
        var identity=client.Session!;
        await client.DisconnectAsync();
        if(scenario=="resume-delayed") {
            await client.ConnectAsync(endpoint,timeout.Token);
            if(client.Session?.MemberId!=identity.MemberId || client.Room?.GetProperty("resumed").GetBoolean()!=true)
                throw new Exception("Reconnect completed before the resumed room snapshot arrived");
        } else {
            var rejected=false;
            try{await client.ConnectAsync(endpoint,timeout.Token);}
            catch(Exception e) when(e is InvalidOperationException or IOException or WebSocketException){rejected=true;}
            if(!rejected)throw new Exception("Unconfirmed room resume reported successful connection");
            if(scenario=="resume-expired" && (client.Session is not null || client.Room is not null))throw new Exception("Expired room identity survived rejection");
        }
        if(input.Count!=0)throw new Exception("Resuming a room dispatched input");
        Console.WriteLine("PASS room resume waits for identity/state, propagates rejection and never replays input");return 0;
    }
}
