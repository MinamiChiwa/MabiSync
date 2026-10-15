using System.Globalization;

namespace MabiSync.Core;

public enum TempoScope { Unknown, Global, PerTrack }
public enum EndScope { Unknown, EntireScore, SelectedTracks }
public sealed record MmlTimingOptions(TempoScope TempoScope = TempoScope.Unknown,
    EndScope EndScope = EndScope.Unknown, IReadOnlyList<int>? SelectedChannels = null, int DefaultTempo = 120);
public sealed record TrackTimeline(int Index, decimal QuarterNotes, double? Seconds)
{
    // TW CalcMusicDuration uses the last event's Time + Duration. A rest has
    // zero Duration; its time still includes every preceding rest.
    public double? LastEventEndSeconds { get; init; }
}
public sealed record ScoreTimeline(bool SyntaxSupported, bool TimingSupported, bool IsLoop,
    IReadOnlyList<TrackTimeline> Tracks, double? FirstCycleSeconds, double? WholeScoreSeconds,
    double? SelectedTracksSeconds, IReadOnlyList<string> Issues)
{
    // A text parser cannot validate a game build or an observed playback onset.
    public string TimingBasis => "standard-mml-unverified-game-semantics";
    public bool GameTimingVerified => false;
    public double? FullPerformanceSeconds => IsLoop ? null : FirstCycleSeconds;
}

public static class MmlTimeline
{
    private sealed record Tempo(decimal Beat, int Value);
    private sealed record Parsed(decimal Beats, IReadOnlyList<Tempo> Tempos, bool NonEmpty, decimal LastEventEnd = 0);
    public static ScoreTimeline Parse(string text, bool isLoop = false, MmlTimingOptions? options = null)
    {
        options ??= new();
        List<string> issues = [];
        if (text is null || text.Length > 4 * 1024 * 1024 || options.DefaultTempo <= 0)
            return new(false, false, isLoop, [], null, null, null, ["Invalid score or default tempo"]);
        var body = text.Trim();
        if (body.StartsWith("MML@", StringComparison.OrdinalIgnoreCase)) body = body[4..];
        if (body.EndsWith(';')) body = body[..^1];
        var tracks = body.Split(',');
        if (tracks.Length > 128) return new(false, false, isLoop, [], null, null, null, ["Too many score tracks"]);
        List<Parsed> parsed = [];
        for (var index = 0; index < tracks.Length; index++)
        {
            try { parsed.Add(ParseTrack(tracks[index])); }
            catch (FormatException error) { issues.Add($"Track {index + 1}: {error.Message}"); parsed.Add(new(0, [], false)); }
        }
        var syntax = issues.Count == 0;
        var selected = options.SelectedChannels?.ToArray() ?? parsed.Select((_, i) => i).ToArray();
        if (selected.Length == 0 || selected.Distinct().Count() != selected.Length || selected.Any(index => index < 0 || index >= parsed.Count)) issues.Add("Invalid selected channel indices");
        var scope = options.TempoScope;
        if (scope == TempoScope.Unknown)
        {
            var nonempty = parsed.Where(t => t.NonEmpty || t.Tempos.Count > 0).ToArray();
            if (nonempty.Length <= 1 || nonempty.All(t => Normalize(t.Tempos, options.DefaultTempo).SequenceEqual(Normalize(nonempty[0].Tempos, options.DefaultTempo)))) scope = TempoScope.PerTrack;
            else issues.Add("Cross-track tempo scope has not been verified");
        }
        var global = new SortedDictionary<decimal, int> { [0] = options.DefaultTempo };
        if (scope == TempoScope.Global)
        {
            // A track may replace a tempo before any note elapses. Only its last
            // command at that beat participates in cross-track conflict checks.
            foreach (var group in parsed.SelectMany(t => t.Tempos.GroupBy(v => v.Beat).Select(v => v.Last())).GroupBy(t => t.Beat))
            {
                var values = group.Select(t => t.Value).Distinct().ToArray();
                if (values.Length != 1) issues.Add("Conflicting global tempo changes at the same beat");
                else global[group.Key] = values[0];
            }
        }
        if (issues.Count > 0)
            return new(syntax, false, isLoop, parsed.Select((t, i) => new TrackTimeline(i, t.Beats, null)).ToArray(), null, null, null, issues);
        var result = parsed.Select((t, i) => new TrackTimeline(i, t.Beats,
            Seconds(t.Beats, scope == TempoScope.Global ? global : Normalize(t.Tempos, options.DefaultTempo)))
            { LastEventEndSeconds = Seconds(t.LastEventEnd, scope == TempoScope.Global ? global : Normalize(t.Tempos, options.DefaultTempo)) }).ToArray();
        var whole = result.Max(t => t.Seconds) ?? 0;
        var subset = selected.Max(i => result[i].Seconds) ?? 0;
        double? cycle = options.EndScope switch
        {
            EndScope.EntireScore => whole,
            EndScope.SelectedTracks => subset,
            _ => Math.Abs(whole - subset) < .000001 ? whole : null
        };
        if (cycle is null) issues.Add("Selected-part versus whole-score ending semantics have not been verified");
        if (!parsed.Any(t => t.NonEmpty)) { cycle = null; issues.Add("Score is empty"); }
        return new(syntax, cycle is not null, isLoop, result, cycle, whole, subset, issues);
    }
    private static SortedDictionary<decimal, int> Normalize(IReadOnlyList<Tempo> changes, int defaultTempo)
    {
        var result = new SortedDictionary<decimal, int> { [0] = defaultTempo };
        foreach (var change in changes) result[change.Beat] = change.Value;
        return result;
    }
    private static double Seconds(decimal end, SortedDictionary<decimal, int> changes)
    {
        decimal previous = 0, seconds = 0; var tempo = changes[0];
        foreach (var (beat, value) in changes)
        {
            if (beat > end) break;
            seconds += (beat - previous) * 60 / tempo;
            previous = beat; tempo = value;
        }
        return (double)(seconds + (end - previous) * 60 / tempo);
    }
    private static Parsed ParseTrack(string source)
    {
        var s = string.Concat(source.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant(); var offset = 0; var defaultLength = 4; var defaultDots = 0;
        var octave = 4; int? lastPitch = null; var tie = false; var nonempty = false;
        decimal beats = 0, lastEventEnd = 0; List<Tempo> tempos = [];
        void Error(string message) => throw new FormatException($"{message} at character {offset}");
        int? Number()
        {
            var start = offset;
            while (offset < s.Length && char.IsAsciiDigit(s[offset])) offset++;
            if (offset == start) return null;
            if (!int.TryParse(s.AsSpan(start, offset - start), NumberStyles.None, CultureInfo.InvariantCulture, out var value)) Error("Number is too large");
            return value;
        }
        int Dots()
        {
            var count = 0; while (offset < s.Length && s[offset] == '.') { count++; offset++; }
            if (count > 16) Error("Unsupported dot count");
            return count;
        }
        while (offset < s.Length)
        {
            var command = s[offset++];
            if (char.IsWhiteSpace(command)) continue;
            if (command == '&')
            {
                if (tie || lastPitch is null) Error("Tie requires a preceding note");
                tie = true; continue;
            }
            if (command is '<' or '>')
            {
                octave += command == '>' ? 1 : -1;
                if (octave is < 0 or > 8) Error("Unsupported octave");
                continue;
            }
            if (command is 'T' or 'V' or 'O' or 'L')
            {
                var number = Number(); if (number is null) Error("Command requires a number");
                var value = number!.Value;
                switch (command)
                {
                    case 'T': if (value <= 0) Error("Tempo must be positive"); tempos.Add(new(beats, value)); break;
                    case 'V': if (value is < 0 or > 15) Error("Unsupported volume"); break;
                    case 'O': if (value is < 0 or > 8) Error("Unsupported octave"); octave = value; break;
                    case 'L': if (value <= 0) Error("Length must be positive"); defaultLength = value; defaultDots = Dots(); break;
                }
                continue;
            }
            int? pitch = null; int? length;
            if (command == 'N')
            {
                pitch = Number(); if (pitch is null or < 0 or > 127) Error("Invalid absolute note");
                length = null;
            }
            else if (command is >= 'A' and <= 'G')
            {
                pitch = octave * 12 + command switch { 'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, _ => 11 };
                if (offset < s.Length && s[offset] is '+' or '#' or '-') pitch += s[offset++] == '-' ? -1 : 1;
                length = Number();
            }
            else if (command == 'R') length = Number();
            else { Error("Unsupported MML token"); return null!; }
            if (length <= 0) Error("Length must be positive");
            if (tie && (pitch is null || pitch != lastPitch)) Error("Tie must connect the same pitch");
            var dots = Dots();
            if (length is null && dots == 0) dots = defaultDots;
            decimal duration = 4m / (length ?? defaultLength), extra = duration / 2;
            for (var index = 0; index < dots; index++) { duration += extra; extra /= 2; }
            lastEventEnd = pitch is null ? beats : beats + duration;
            beats += duration; nonempty = true; lastPitch = pitch; tie = false;
        }
        if (tie) Error("Unterminated tie");
        return new(beats, tempos, nonempty, lastEventEnd);
    }
}
