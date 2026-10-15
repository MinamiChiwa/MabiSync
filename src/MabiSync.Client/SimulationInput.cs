using MabiSync.Core;

namespace MabiSync.Client;

public sealed class SimulationInput(IMonotonicClock clock) : IInputExecutor
{
    private int _count;
    public int Count => Volatile.Read(ref _count);
    public InputPreparation Prepare(ScheduledAction action) => new(true, null);
    public InputOutcome Execute(ScheduledAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sent = clock.NowMs; Interlocked.Increment(ref _count);
        return new("executed", sent, true, "Simulation only; no game input");
    }
    public void ReleaseAll() { }
}
