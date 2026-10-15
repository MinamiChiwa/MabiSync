namespace MabiSync.Core;

public sealed record ScheduledAction(string Id, string SessionId, string Kind, double TargetServerMs, string ServerEpoch)
{
    public string Mode { get; init; } = "emote";
    public bool LocalOnly { get; init; }
}
public sealed record InputPreparation(bool Ready, string? Error);
public sealed record InputOutcome(string Status, double? SentLocalMs, bool Simulated, string? Detail);
public sealed record ActionReceipt(string ActionId, string SessionId, string Status, double PlannedLocalMs,
    double? ActualLocalMs, double? ActualServerMs, double? ClockUncertaintyMs, bool Simulated, string? Detail)
{
    public bool GameResponseVerified => false;
    public bool ReportToRoom { get; init; } = true;
    public int AppliedTimingOffsetMs { get; init; }
}
public interface IInputExecutor
{
    bool IsSimulation(ScheduledAction action) => true;
    InputPreparation Prepare(ScheduledAction action);
    InputOutcome Execute(ScheduledAction action, CancellationToken cancellationToken);
    void Forget(string actionId) { }
    void ReleaseAll();
}
