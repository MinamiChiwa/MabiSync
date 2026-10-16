using System.Collections.Concurrent;
using System.Threading.Channels;
using MabiSync.Core;

namespace MabiSync.Client;

public sealed partial class RoomClient
{
    private sealed record PreparationJob(ScheduledAction Action,bool Manual,SessionIdentity? Owner,CancellationTokenSource Cancellation);
    private readonly Channel<PreparationJob> _preparationQueue=Channel.CreateUnbounded<PreparationJob>(new(){SingleReader=true});
    private readonly ConcurrentDictionary<string,PreparationJob> _preparations=new();
    private readonly Task _preparationPump;
    /// <summary>Restore the locally selected window before readiness acknowledgment. Never dispatch input here.</summary>
    public Func<ScheduledAction,CancellationToken,Task>? PrepareLocalAsync {get;set;}
    public void CancelLocalPreparations()=>CancelPreparations();
    private void QueuePreparation(ScheduledAction action,bool manual)
    {
        var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_connection?.Token??CancellationToken.None);
        var job=new PreparationJob(action,manual,Session,cancellation);
        if(!_preparations.TryAdd(action.Id,job)){cancellation.Dispose();return;}
        if(!_preparationQueue.Writer.TryWrite(job)){_preparations.TryRemove(action.Id,out _);cancellation.Dispose();}
    }
    private void CancelPreparations(string? sessionId=null,string? actionId=null)
    {
        foreach(var job in _preparations.Values.Where(j=>(sessionId is null || j.Action.SessionId==sessionId) && (actionId is null || j.Action.Id==actionId)))
            try{job.Cancellation.Cancel();}catch(ObjectDisposedException){}
    }
    private async Task PreparationLoop()
    {
        await foreach(var job in _preparationQueue.Reader.ReadAllAsync().ConfigureAwait(false)) {
            var action=job.Action;var token=job.Cancellation.Token;
            try {
                token.ThrowIfCancellationRequested();
                InputPreparation result;
                try {
                    if(job.Owner is null || job.Owner!=Session || action.ServerEpoch!=_epoch)result=new(false,"Not in the selected room");
                    else if(!job.Manual && _prepared.TryGetValue(action.Id,out var existing))result=new(existing==action,existing==action?null:"Conflicting prepared action");
                    else {
                        // Run off the receive loop so cancel, stop and disconnect remain responsive.
                        if((job.Manual || action.Kind=="start") && _activeSession!=action.SessionId && PrepareLocalAsync is{} prepare)
                            await prepare(action,token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if(job.Owner!=Session || action.ServerEpoch!=_epoch)continue;
                        result=job.Manual
                            ?action.Mode=="ensemble"?_scheduler.ValidateMusicStopPath(action.SessionId,action.ServerEpoch,action.Mode):new(false,"Invalid manual join session")
                            :_scheduler.Prepare(action);
                    }
                }catch(OperationCanceledException) when(token.IsCancellationRequested){continue;}
                catch(Exception error){result=new(false,error.Message);}
                if(token.IsCancellationRequested){if(!job.Manual)_scheduler.CancelAction(action.Id);continue;}
                if(result.Ready){if(!job.Manual)_prepared[action.Id]=action;_activeSession=action.SessionId;_activeMode=action.Mode;}
                if(job.Manual)await SendAsync(new{type="session-ready",sessionId=action.SessionId,ready=result.Ready,error=result.Error}).ConfigureAwait(false);
                else await SendAsync(new{type="action-ready",actionId=action.Id,ready=result.Ready,error=result.Error}).ConfigureAwait(false);
            }catch(OperationCanceledException) when(token.IsCancellationRequested){}
            catch(Exception error){if(Connected)FireNotice("本机动作准备未完成："+error.Message);}
            finally{_preparations.TryRemove(action.Id,out _);job.Cancellation.Dispose();}
        }
    }
}
