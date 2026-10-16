using System.Collections.Concurrent;
using System.Text.Json;
using MabiSync.Client;
using MabiSync.Core;

internal static class FocusRoomFlow
{
    public static async Task<int> Run(string endpoint)
    {
        using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(18));
        var clock=new StopwatchClock();var roomClock=new RoomClock(clock);var platform=new Platform();
        var bindings=new ModeBindings(new KeyBinding(0x41),new KeyBinding(0x42));
        var game=new GameInputExecutor(clock,platform,()=>new(Platform.Window,bindings,bindings,bindings),new UnknownPlaybackEvidence());
        using var scheduler=new ActionScheduler(clock,roomClock,game);await using var member=new RoomClient(clock,roomClock,scheduler);
        var hostClock=new StopwatchClock();var hostRoom=new RoomClock(hostClock);using var hostScheduler=new ActionScheduler(hostClock,hostRoom,new SimulationInput(hostClock));
        await using var host=new RoomClient(hostClock,hostRoom,hostScheduler);
        async Task Wait(Func<bool> condition){while(!condition())await Task.Delay(5,limit.Token);}
        int activated=0;
        member.PrepareLocalAsync=async(_,token)=>{await Task.Delay(150,token);platform.Restore();Interlocked.Increment(ref activated);};
        try {
            await host.ConnectAsync(endpoint);await host.CreateRoomAsync("Focus host");await member.ConnectAsync(endpoint);await member.JoinRoomAsync("Focus member",host.Session!.Code);
            await Wait(()=>host.Room?.GetProperty("members").GetArrayLength()==2);var ids=new[]{host.Session.MemberId,member.Session!.MemberId};
            foreach(var initiator in new[]{true,false}) {
                await host.UpdateRosterAsync("edit-ensemble-member",member.Session.MemberId,group:initiator?2:1,musicRole:initiator?"initiator":"joiner");
                await host.RefreshClockAsync();await member.RefreshClockAsync();platform.Minimize();
                Check(game.Prepare(new("readiness","validation","stop",0,member.Session.ServerEpoch){Mode="ensemble"}).Ready,"Minimized readiness failed");game.Forget("readiness");
                await host.SendAsync(new{type="configure-input",capabilities=new{simulation=true,canStart=true,canStop=true,readyMode="ensemble"}});
                await member.SendAsync(new{type="set-ready",ready=true,capabilities=new{simulation=false,canStart=initiator,canStop=true,readyMode="ensemble",windowActivationMs=1500}});
                await Wait(()=>host.Room?.GetProperty("members").EnumerateArray().Any(m=>m.GetProperty("id").GetString()==member.Session.MemberId && m.GetProperty("ready").GetBoolean())==true);
                var inputs=platform.Events.Count;
                await host.SendAsync(new{type="prepare",mode="ensemble",countdownMs=1000,participantIds=ids});
                await Wait(()=>host.Room?.GetProperty("performance") is{ValueKind:JsonValueKind.Object} p && p.GetProperty("phase").GetString()=="performing");
                await Wait(()=>platform.Events.Count>=inputs+(initiator?2:0));
                Check(platform.State.Foreground && platform.Events.Count==inputs+(initiator?2:0),"Member did not restore before start, or manual joiner received input");
                await host.SendAsync(new{type="stop-all"});await Wait(()=>host.Room?.GetProperty("performance").GetProperty("phase").GetString()=="stopped");
                await Wait(()=>platform.Events.Count==inputs+(initiator?4:2));
            }
            Check(activated==2,"Window preparation was not once per round");
            await host.UpdateRosterAsync("edit-ensemble-member",member.Session.MemberId,group:2,musicRole:"initiator");
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var cancelled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            member.PrepareLocalAsync=async(_,token)=>{entered.TrySetResult();try{await Task.Delay(5000,token);}catch(OperationCanceledException){cancelled.TrySetResult();throw;}};
            await member.SendAsync(new{type="set-ready",ready=true,capabilities=new{simulation=false,canStart=true,canStop=true,readyMode="ensemble",windowActivationMs=1500}});
            await host.SendAsync(new{type="configure-input",capabilities=new{simulation=true,canStart=true,canStop=true,readyMode="ensemble"}});
            await Wait(()=>host.Room?.GetProperty("members").EnumerateArray().Any(m=>m.GetProperty("id").GetString()==member.Session.MemberId && m.GetProperty("ready").GetBoolean())==true);
            var before=platform.Events.Count;await host.SendAsync(new{type="prepare",mode="ensemble",countdownMs=5000,participantIds=ids});
            await entered.Task.WaitAsync(limit.Token);await host.SendAsync(new{type="cancel"});await cancelled.Task.WaitAsync(TimeSpan.FromMilliseconds(700));
            await Wait(()=>member.Room?.GetProperty("performance").GetProperty("phase").GetString()=="cancelled");
            Check(platform.Events.Count==before && member.Connected,"Cancellation blocked behind activation or dispatched input");
            Console.WriteLine("PASS minimized/background member activation, manual joiner, and responsive cancellation; desktop inputs: 0");return 0;
        }catch(Exception error){Console.WriteLine("FAIL focus flow: "+error);return 1;}
    }
    private static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
    private sealed class Platform:IGameInputPlatform
    {
        public static readonly GameWindowId Window=new(1,2,3,"fixture.exe");
        public GameWindowState State=new(Window,new(0,0,0,0,96),false,false,true);
        public ConcurrentQueue<string> Events=new();
        public void Minimize()=>State=State with{Visible=false,Foreground=false,Client=new(0,0,0,0,96)};
        public void Restore()=>State=State with{Visible=true,Foreground=true,Client=new(0,0,1280,960,96)};
        public GameWindowState? Inspect(GameWindowId window)=>State;
        public bool KeyIsDown(ushort key)=>false;
        public bool LeftButtonIsDown()=>false;
        public bool PointBelongsToWindow(GameWindowId window,int x,int y)=>true;
        public void Key(ushort key,bool down)=>Events.Enqueue($"{key}:{down}");
        public void MovePointer(int x,int y)=>throw new Exception("Unexpected pointer input");
        public void LeftButton(bool down)=>throw new Exception("Unexpected click");
    }
}
