using System.Diagnostics;

namespace MabiSync.Core;

public interface IMonotonicClock { double NowMs { get; } }
public sealed class StopwatchClock : IMonotonicClock
{
    public double NowMs => Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency);
}
public sealed record ClockEstimate(double OffsetMs, double RoundTripMs, double UncertaintyMs, double AgeMs);

public sealed class RoomClock(IMonotonicClock clock)
{
    private readonly object _gate = new();
    private readonly List<(double Offset, double Rtt, double Received)> _samples = [];
    public bool AddSample(double t1, double t2, double t3, double t4)
    {
        if (!new[] { t1, t2, t3, t4 }.All(double.IsFinite) || t4 < t1 || t3 < t2) return false;
        var rtt = (t4 - t1) - (t3 - t2);
        if (rtt < -.000001) return false;
        lock (_gate)
        {
            _samples.Add((((t2 - t1) + (t3 - t4)) / 2, Math.Max(0, rtt), t4));
            if (_samples.Count > 64) _samples.RemoveAt(0);
        }
        return true;
    }
    public ClockEstimate? Estimate
    {
        get
        {
            lock (_gate)
            {
                var now = clock.NowMs;
                _samples.RemoveAll(s => now - s.Received > 10_000);
                if (_samples.Count == 0) return null;
                var minimum = _samples.Min(s => s.Rtt);
                var best = _samples.Where(s => s.Rtt <= minimum + Math.Max(2, minimum * .1))
                    .OrderByDescending(s => s.Received).Take(9).ToArray();
                var offsets = best.Select(s => s.Offset).Order().ToArray();
                var offset = offsets.Length % 2 == 1 ? offsets[offsets.Length / 2] : (offsets[offsets.Length / 2 - 1] + offsets[offsets.Length / 2]) / 2;
                var sample = best.MinBy(s => s.Rtt);
                var age = Math.Max(0, now - sample.Received);
                // Network asymmetry plus a stated drift allowance, not an OS/game precision claim.
                return new(offset, sample.Rtt, sample.Rtt / 2 + age * .0002 + offsets.Select(o=>Math.Abs(o-offset)).Order().ElementAt(offsets.Length/2), age);
            }
        }
    }
    public double? ServerNowMs => Estimate is { } estimate ? clock.NowMs + estimate.OffsetMs : null;
    public void Reset() { lock (_gate) _samples.Clear(); }
}
