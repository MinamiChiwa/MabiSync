using System.Collections.Concurrent;
using System.Text.Json;
using MabiSync.Client;
using MabiSync.Core;

internal static class TimingRoomFlow
{
    public static async Task<int> Run(string endpoint)
    {
        using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(18));
        await using var host=new Peer();await using var member=new Peer();
        async Task Wait(Func<bool> condition){while(!condition())await Task.Delay(5,limit.Token);}
        try {
            await host.Client.ConnectAsync(endpoint);await member.Client.ConnectAsync(endpoint);
            await host.Client.CreateRoomAsync("Timing host");await member.Client.JoinRoomAsync("Early member",host.Client.Session!.Code);
            await Wait(()=>host.Client.Room?.GetProperty("members").GetArrayLength()==2);
            await host.Client.UpdateRosterAsync("edit-ensemble-member",member.Client.Session!.MemberId,group:2,musicRole:"initiator");
            var ids=new[]{host.Client.Session.MemberId,member.Client.Session.MemberId};var skews=new List<double>();
            foreach(var offset in new[]{-125,-2000,125}) {
                member.Scheduler.TimingOffsetMs=offset;
                await host.Client.RefreshClockAsync();await member.Client.RefreshClockAsync();
                await host.Ready();await member.Ready();
                await Wait(()=>host.Client.Room?.GetProperty("members").EnumerateArray().All(m=>m.GetProperty("ready").GetBoolean())==true);
                var old=host.Client.Room?.GetProperty("performance");var oldId=old is{ValueKind:JsonValueKind.Object}?old.Value.GetProperty("id").GetString():null;
                await host.Client.SendAsync(new{type="prepare",mode="ensemble",countdownMs=1000,participantIds=ids});
                await Wait(()=>host.Client.Room?.GetProperty("performance") is{ValueKind:JsonValueKind.Object} p && p.GetProperty("id").GetString()!=oldId && p.GetProperty("phase").GetString()=="committed");
                var round=host.Client.Room!.Value.GetProperty("performance").GetProperty("id").GetString();
                await Wait(()=>host.Receipts.Any(r=>r.SessionId==round && r.Status=="executed") && member.Receipts.Any(r=>r.SessionId==round && r.Status=="executed"));
                var a=host.Receipts.Single(r=>r.SessionId==round);var b=member.Receipts.Single(r=>r.SessionId==round);
                if(b.AppliedTimingOffsetMs!=offset)throw new InvalidOperationException("Member adjustment was not frozen");
                skews.Add(Math.Abs(a.ActualLocalMs!.Value-a.AppliedTimingOffsetMs-(b.ActualLocalMs!.Value-b.AppliedTimingOffsetMs)));
                await host.Client.SendAsync(new{type="stop-all"});
                await Wait(()=>host.Client.Room?.GetProperty("performance").GetProperty("phase").GetString()=="stopped");
                await Task.Delay(100,limit.Token);
            }
            Console.WriteLine(JsonSerializer.Serialize(new{status="passed",rounds=3,adjustments=new[]{-125,-2000,125},correctedToolSkewMs=skews,desktopInputs=0}));return 0;
        }catch(Exception error){Console.WriteLine(JsonSerializer.Serialize(new{status="failed",error=error.Message,host=host.Notices.ToArray(),member=member.Notices.ToArray()}));return 1;}
    }
    private sealed class Peer:IAsyncDisposable
    {
        private readonly StopwatchClock _clock=new();private readonly RoomClock _room;
        public readonly ActionScheduler Scheduler;public readonly RoomClient Client;
        public readonly ConcurrentQueue<ActionReceipt> Receipts=new();public readonly ConcurrentQueue<string> Notices=new();
        public Peer(){_room=new(_clock);Scheduler=new(_clock,_room,new SimulationInput(_clock));Client=new(_clock,_room,Scheduler);Client.ReceiptReported+=Receipts.Enqueue;Client.Notice+=Notices.Enqueue;}
        public Task Ready()=>Client.SendAsync(new{type="set-ready",ready=true,capabilities=new{simulation=true,canStart=true,canStop=true,readyMode="ensemble",timingOffsetMs=Scheduler.TimingOffsetMs,clockRoundTripMs=_room.Estimate?.RoundTripMs??0}});
        public async ValueTask DisposeAsync(){await Client.DisposeAsync();Scheduler.Dispose();}
    }
}
