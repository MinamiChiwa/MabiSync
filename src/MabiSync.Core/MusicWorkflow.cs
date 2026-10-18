using System.Globalization;

namespace MabiSync.Core;

public enum MusicStopRule { Manual, Duration, FirstCycle }

public static class MusicStopSettings
{
    public static MusicStopRule Restore(string? name, int? legacyIndex) => name switch
    {
        "manual" => MusicStopRule.Manual,
        "duration" => MusicStopRule.Duration,
        "first-cycle" => MusicStopRule.FirstCycle,
        _ => name is null && legacyIndex is null ? MusicStopRule.FirstCycle : legacyIndex == 2 ? MusicStopRule.Duration : MusicStopRule.Manual
    };
    public static string Persist(MusicStopRule rule) => rule switch { MusicStopRule.Duration => "duration", MusicStopRule.FirstCycle => "first-cycle", _ => "manual" };
}

public static class MusicBindingSettings
{
    public static ModeBindings Restore(IReadOnlyDictionary<string, ModeBindings> saved)
    {
        saved.TryGetValue("ensemble", out var current);
        if (current?.Start is not null || current?.Stop is not null) return current;
        return saved.TryGetValue("independent", out var legacy) && legacy is not null ? legacy : current ?? new();
    }
}

public static class MusicDurationText
{
    public static long ParseMilliseconds(string text)
    {
        if (!decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
            seconds is < .001m or > 86400 || seconds * 1000 != decimal.Truncate(seconds * 1000))
            throw new FormatException("手动演奏时长请填写 0.001–86400 秒，最多三位小数，例如 30 或 12.5。");
        return (long)(seconds * 1000);
    }
}

public static class MusicTailText
{
    public static long ParseMilliseconds(string text)
    {
        if (!decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
            seconds is < 0 or > 10 || seconds * 1000 != decimal.Truncate(seconds * 1000))
            throw new FormatException("尾音保留请填写 0–10 秒，最多三位小数，例如 1 或 1.5。");
        return (long)(seconds * 1000);
    }
}

public sealed record ImportedMml(string Name, ScoreTimeline Timeline)
{
    public bool AutomaticEndAvailable => false;
    public long? SuggestedManualMilliseconds => Timeline is { TimingSupported: true, FirstCycleSeconds: > 0 and <= 86400 } &&
        double.IsFinite(Timeline.FirstCycleSeconds.Value) ? (long)Math.Ceiling(Timeline.FirstCycleSeconds.Value * 1000) : null;
    public static ImportedMml Parse(string name, string text, bool loop = false) => new(name, MmlTimeline.Parse(text, loop));
}

public sealed record MusicRoundActions(bool CanReportInterruption, bool CanContinue, bool CanStopAll)
{
    public static MusicRoundActions For(string mode, string? phase, bool isHost, bool isLeader, bool startReached)
    {
        var active = mode is "independent" or "ensemble" && phase is "preparing" or "committed" or "performing" or "interrupted";
        return new(active && isLeader && startReached && phase != "interrupted",
            active && isHost && phase == "interrupted", active && isHost);
    }
}
