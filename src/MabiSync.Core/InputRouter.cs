namespace MabiSync.Core;

/// <summary>Freezes the selected executor with each prepared action, independent of later UI changes.</summary>
public sealed class InputRouter(IInputExecutor simulation, IInputExecutor game) : IInputExecutor
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IInputExecutor> _routes = [];
    private volatile bool _useSimulation = true;
    private bool _startsEnabled = true;
    private string? _readyMode;
    public string? ReadyMode { get { lock (_gate) return _readyMode; } set { lock (_gate) _readyMode = value; } }
    public bool StartsEnabled { get { lock (_gate) return _startsEnabled; } set { lock (_gate) _startsEnabled = value; } }
    public bool UseSimulation { get => _useSimulation; set => _useSimulation = value; }
    public bool IsSimulation(ScheduledAction action) { lock (_gate) return (_routes.GetValueOrDefault(action.Id) ?? (UseSimulation ? simulation : game)).IsSimulation(action); }
    public InputPreparation Prepare(ScheduledAction action)
    {
        lock (_gate)
        {
            if (action.Kind == "start" && !_startsEnabled) return new(false, "你尚未就绪，或刚刚修改了设置。请重新点击“就绪”。");
            if (action.Kind == "start" && _readyMode is not null && _readyMode != action.Mode) return new(false, "房主已更改本轮模式，请确认设置后重新就绪。");
            var executor = UseSimulation ? simulation : game;
            var ready = executor.Prepare(action);
            if (ready.Ready) _routes[action.Id] = executor;
            return ready;
        }
    }
    public InputOutcome Execute(ScheduledAction action, CancellationToken token)
    {
        IInputExecutor? executor; lock (_gate) executor = _routes.GetValueOrDefault(action.Id);
        return executor is null ? new("failed", null, IsSimulation(action), "本次操作尚未准备好，请重新就绪后再开始。") : executor.Execute(action, token);
    }
    public void Forget(string actionId) { lock (_gate) { if (_routes.Remove(actionId, out var executor)) executor.Forget(actionId); } }
    public void ReleaseAll() { simulation.ReleaseAll(); game.ReleaseAll(); }
}
