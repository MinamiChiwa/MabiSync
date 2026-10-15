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
            if (action.Kind == "start" && !_startsEnabled) return new(false, "本机尚未就绪或输入设置正在变更，请重新就绪。");
            if (action.Kind == "start" && _readyMode is not null && _readyMode != action.Mode) return new(false, "房主安排的动作模式与本机准备模式不同，请核对后重新就绪。");
            var executor = UseSimulation ? simulation : game;
            var ready = executor.Prepare(action);
            if (ready.Ready) _routes[action.Id] = executor;
            return ready;
        }
    }
    public InputOutcome Execute(ScheduledAction action, CancellationToken token)
    {
        IInputExecutor? executor; lock (_gate) executor = _routes.GetValueOrDefault(action.Id);
        return executor is null ? new("failed", null, IsSimulation(action), "动作没有已准备的本机输入路径。") : executor.Execute(action, token);
    }
    public void Forget(string actionId) { lock (_gate) { if (_routes.Remove(actionId, out var executor)) executor.Forget(actionId); } }
    public void ReleaseAll() { simulation.ReleaseAll(); game.ReleaseAll(); }
}
