using System.Diagnostics;

namespace MabiSync.Core;

/// <summary>One owned worker. No UI dispatch, wall clock, packet or process API.</summary>
public sealed class ActionScheduler : IDisposable
{
    private sealed class Entry(ScheduledAction action, double deadline, ClockEstimate? estimate, bool simulated, int timingOffsetMs = 0)
    {
        public ScheduledAction Action { get; } = action;
        public double Deadline { get; set; } = deadline;
        public ClockEstimate? Estimate { get; set; } = estimate;
        public bool Committed { get; set; }
        public bool Executing { get; set; }
        public bool Immediate {get;init;}
        public bool Simulated { get; } = simulated;
        public int TimingOffsetMs { get; } = timingOffsetMs;
        public CancellationTokenSource Cancellation { get; } = new();
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _pending = [];
    private readonly HashSet<string> _preparing = [];
    private readonly HashSet<string> _completed = [];
    private readonly Queue<string> _history = new();
    private readonly HashSet<string> _stopRequests = [];
    private readonly Dictionary<string,int> _sessionOffsets = [];
    private readonly AutoResetEvent _wake = new(false);
    private readonly IMonotonicClock _local;
    private readonly RoomClock _room;
    private readonly IInputExecutor _input;
    private readonly Thread _thread;
    private bool _disposed;
    private string? _epoch;
    private long _cancellationVersion;
    private int _timingOffsetMs;
    public int TimingOffsetMs
    {
        get { lock (_gate) return _timingOffsetMs; }
        set { if (value is < -2000 or > 2000) throw new ArgumentOutOfRangeException(nameof(value)); lock (_gate) _timingOffsetMs = value; }
    }
    public double? LocalDeadline(string actionId) { lock (_gate) return _pending.TryGetValue(actionId, out var entry) ? entry.Deadline : null; }
    public event Action<ActionReceipt>? Receipt;
    public ActionScheduler(IMonotonicClock local, RoomClock room, IInputExecutor input)
    {
        _local = local; _room = room; _input = input;
        _thread = new Thread(Run) { IsBackground = true, Name = "MabiSync action scheduler", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }
    public void ChangeEpoch(string epoch)
    {
        if (string.IsNullOrWhiteSpace(epoch)) throw new ArgumentException("Missing service epoch", nameof(epoch));
        lock (_gate) { if (_epoch == epoch) return; _epoch = epoch; }
        CancelWhere(_ => true, "服务器已重启或连接地址已改变，本轮操作已取消。", releaseAll: true);
        lock (_gate) { _completed.Clear(); _history.Clear(); _stopRequests.Clear(); _sessionOffsets.Clear(); }
        _room.Reset();
    }
    private static bool Valid(ScheduledAction a) =>
        !string.IsNullOrWhiteSpace(a.Id) && a.Id.Length <= 128 &&
        !string.IsNullOrWhiteSpace(a.SessionId) && a.SessionId.Length <= 128 &&
        a.Kind is "start" or "stop" && a.Mode is "emote" or "independent" or "ensemble" &&
        !(a.Mode == "emote" && a.Kind == "stop") && double.IsFinite(a.TargetServerMs);
    public InputPreparation Prepare(ScheduledAction action)
    {
        var estimate = _room.Estimate;
        if (estimate is null) return new(false, "正在同步时钟，请稍后再开始。");
        double deadline; int lead;
        long version;
        lock (_gate)
        {
            if (_disposed || !Valid(action) || action.ServerEpoch != _epoch) return new(false, "本轮操作已失效，请重新连接房间后再开始。");
            if (_completed.Contains(action.Id) || _pending.ContainsKey(action.Id) || _preparing.Contains(action.Id)) return new(false, "本次操作已安排或执行过。");
            lead = _sessionOffsets.GetValueOrDefault(action.SessionId, _timingOffsetMs);
            deadline = action.TargetServerMs - estimate.OffsetMs + lead;
            if (action.Kind == "start" && deadline < _local.NowMs - 100) return new(false, "已错过开始时间，请房主重新开始本轮。");
            version = _cancellationVersion; _preparing.Add(action.Id);
        }
        InputPreparation preparation;
        try { preparation = _input.Prepare(action); }
        catch { lock (_gate) _preparing.Remove(action.Id); _input.Forget(action.Id); throw; }
        var accepted = false;
        lock (_gate)
        {
            _preparing.Remove(action.Id);
            if (preparation.Ready && !_disposed && version == _cancellationVersion && action.ServerEpoch == _epoch && !_pending.ContainsKey(action.Id) && !_completed.Contains(action.Id))
            {
                FreezeOffset(action.SessionId,lead);
                _pending.Add(action.Id, new(action, deadline, estimate, _input.IsSimulation(action), lead)); accepted = true;
            }
        }
        if (!accepted) _input.Forget(action.Id);
        return accepted ? new(true, null) : preparation.Ready ? new(false, "本轮设置已改变，请重新就绪后再开始。") : preparation;
    }
    private void FreezeOffset(string sessionId,int offset)
    {
        if (_sessionOffsets.ContainsKey(sessionId)) return;
        if (_sessionOffsets.Count>=4096) _sessionOffsets.Remove(_sessionOffsets.Keys.First());
        _sessionOffsets[sessionId]=offset;
    }
    public bool Commit(ScheduledAction action)
    {
        lock (_gate)
        {
            if (_disposed || !_pending.TryGetValue(action.Id, out var entry) || entry.Action != action || entry.Committed || action.ServerEpoch != _epoch) return false;
            var estimate = _room.Estimate;
            if (estimate is null) return false;
            entry.Estimate = estimate;
            entry.Deadline = action.TargetServerMs - estimate.OffsetMs + entry.TimingOffsetMs;
            entry.Committed = true;
        }
        Signal(); return true;
    }
    /// <summary>Checks a manual joiner's stop binding without scheduling an input or generating a receipt.</summary>
    public InputPreparation ValidateMusicStopPath(string sessionId, string epoch, string mode)
    {
        var check = new ScheduledAction(Guid.NewGuid().ToString(), sessionId, "stop", 0, epoch) { Mode = mode };
        long version;
        lock (_gate)
        {
            if (_disposed || !Valid(check) || epoch != _epoch) return new(false, "本轮演奏已失效，请重新连接房间后再开始。");
            version = _cancellationVersion;
        }
        try
        {
            var result = _input.Prepare(check);
            lock (_gate)
            {
                if (_disposed || epoch != _epoch || version != _cancellationVersion) return new(false,"本轮演奏状态已改变，请重新就绪后再开始。");
                if (result.Ready) FreezeOffset(sessionId,_timingOffsetMs);
                return result;
            }
        }
        finally { _input.Forget(check.Id); }
    }
    public void CancelAction(string actionId) => CancelWhere(e => e.Action.Id == actionId, "本次操作已取消。");
    public void TransportDisconnected() => CancelWhere(e => e.Action.Kind == "start" || !e.Committed, "房间连接已断开，尚未执行的开始操作已取消。");
    /// <summary>Leaving a room removes its future inputs; a stop sent as part of closure may finish releasing normally.</summary>
    public void RoomExited()=>CancelWhere(e=>!e.Immediate,"已退出房间，尚未执行的操作已取消。");
    public void CancelAll() => CancelWhere(_ => true, "已在本机取消操作。", releaseAll: true);
    public void CancelPendingStarts() => CancelWhere(e => e.Action.Kind == "start" || !e.Committed, "本机设置或就绪状态已改变，尚未执行的操作已取消。");
    public bool StopNow(string id, string sessionId, string epoch, string mode, bool localOnly = false)
    {
        var action = new ScheduledAction(id, sessionId, "stop", 0, epoch) { Mode = mode, LocalOnly = localOnly };
        lock (_gate)
        {
            if (_disposed || !Valid(action) || epoch != _epoch || _completed.Contains(id) || _pending.ContainsKey(id) || !_stopRequests.Add(id)) return false;
        }
        // Reserve first: a duplicate or stale stop must not cancel a valid task.
        CancelWhere(e => e.Action.SessionId == sessionId, "已取消原定操作，改为立即停止。", releaseAll: true);
        var preparation = _input.Prepare(action);
        if (!preparation.Ready)
        {
            var failed = new Entry(action, _local.NowMs, _room.Estimate, _input.IsSimulation(action));
            lock (_gate) Remember(id);
            Publish(failed, new("failed", null, failed.Simulated, preparation.Error));
            _input.Forget(id); failed.Cancellation.Dispose(); return false;
        }
        lock (_gate)
        {
            if (_disposed || epoch != _epoch || _completed.Contains(id) || _pending.ContainsKey(id)) return false;
            _pending.Add(id, new(action, _local.NowMs, _room.Estimate, _input.IsSimulation(action)) { Committed = true,Immediate=true });
        }
        Signal(); return true;
    }
    private void Remember(string id)
    {
        _completed.Add(id); _history.Enqueue(id);
        if (_history.Count > 4096) _completed.Remove(_history.Dequeue());
    }
    private void CancelWhere(Func<Entry, bool> predicate, string detail, bool releaseAll = false)
    {
        List<Entry> cancelled = [];
        lock (_gate)
        {
            _cancellationVersion++;
            foreach (var entry in _pending.Values.Where(predicate).ToArray())
            {
                entry.Cancellation.Cancel();
                if (entry.Executing) { releaseAll = true; continue; }
                _pending.Remove(entry.Action.Id); Remember(entry.Action.Id); cancelled.Add(entry);
            }
        }
        if (releaseAll) _input.ReleaseAll();
        foreach (var entry in cancelled)
        {
            Publish(entry, new("cancelled", null, entry.Simulated, detail));
            _input.Forget(entry.Action.Id);
            entry.Cancellation.Dispose();
        }
        Signal();
    }
    private void Signal() { try { _wake.Set(); } catch (ObjectDisposedException) { } }
    private void Run()
    {
        using var waiter=new DeadlineWaiter();
        try
        {
            while (true)
            {
                Entry? next; double wait;
                lock (_gate)
                {
                    if (_disposed) return;
                    next = _pending.Values.Where(e => e.Committed && !e.Executing).MinBy(e => e.Deadline);
                    wait = next is null ? 250 : next.Deadline - _local.NowMs;
                    if (next is not null && wait <= 0) next.Executing = true;
                }
                if(next is null){_wake.WaitOne(250);continue;}
                if(wait>1){waiter.Wait(_wake,wait);continue;}
                if (wait > 0) { Thread.Yield(); continue; }
                InputOutcome outcome;
                try
                {
                    if (next.Cancellation.IsCancellationRequested) outcome = new("cancelled", null, next.Simulated, "执行前已取消。");
                    else if (next.Action.Kind == "start" && _local.NowMs - next.Deadline > 100) outcome = new("missed", null, next.Simulated, "未能按时开始，本次操作已跳过。请房主重新开始本轮。");
                    else outcome = _input.Execute(next.Action, next.Cancellation.Token);
                }
                catch (OperationCanceledException) { outcome = new("cancelled", null, next.Simulated, "本次操作已取消。"); }
                catch (Exception error) { outcome = new("failed", null, next.Simulated, error.Message); }
                lock (_gate) { _pending.Remove(next.Action.Id); Remember(next.Action.Id); }
                Publish(next, outcome); _input.Forget(next.Action.Id); next.Cancellation.Dispose();
            }
        }
        finally { _wake.Dispose(); }
    }
    private void Publish(Entry entry, InputOutcome outcome)
    {
        var estimate = entry.Estimate;
        var receipt = new ActionReceipt(entry.Action.Id, entry.Action.SessionId, outcome.Status, entry.Deadline,
            outcome.SentLocalMs, outcome.SentLocalMs + estimate?.OffsetMs,
            estimate?.UncertaintyMs, outcome.Simulated, outcome.Detail) { ReportToRoom = !entry.Action.LocalOnly, AppliedTimingOffsetMs = entry.TimingOffsetMs };
        if (Receipt is not { } handlers) return;
        foreach (Action<ActionReceipt> handler in handlers.GetInvocationList())
            try { handler(receipt); } catch (Exception error) { Trace.WriteLine("Receipt observer failed: " + error.GetType().Name); }
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        CancelWhere(_ => true, "工具已关闭，尚未执行的操作已取消。", releaseAll: true);
        if (Thread.CurrentThread != _thread) _thread.Join(2000);
    }
}
