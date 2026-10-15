using System.Security.Cryptography;
using System.Text;

namespace MabiSync.Core;

/// <summary>Content-only TW score decoding. Does not establish playback onset or loop period.</summary>
internal sealed record TwScoreTimeline(string? Sha256, ScoreTimeline? Timeline, string? Issue)
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    public static TwScoreTimeline Decode(ReadOnlySpan<byte> blob, bool loop)
    {
        if (blob.Length > TwGameProtocol.MaximumBody) return new(null, null, "谱面超过可解析大小。");
        // Four envelope bytes are opaque, never an allocation length.
        for (var candidate = 0; candidate < 3; candidate++)
        {
            try
            {
                var expanded = candidate switch
                {
                    0 => blob.ToArray(),
                    1 when blob.Length > 4 => TwGameProtocol.Expand(blob[4..], 1),
                    2 => TwGameProtocol.Expand(blob, 1),
                    _ => throw new InvalidDataException()
                };
                if (!expanded.AsSpan().StartsWith("MML@"u8)) continue;
                var text = Utf8.GetString(expanded).TrimEnd('\0', '\r', '\n', ' ');
                var hash = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(text))).ToLowerInvariant();
                var timeline = MmlTimeline.Parse(text, loop, new(TempoScope.Global, EndScope.EntireScore));
                return new(hash, timeline, timeline.TimingSupported ? null : "谱面包含尚未支持的语法或速度变化。");
            }
            catch (Exception error) when (error is InvalidDataException or DecoderFallbackException) { }
        }
        return new(null, null, "暂时无法解析这张乐谱。");
    }
}
