using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace MabiSync.Core;

public sealed record GameIdentity(ulong CharacterId, int RealmId, string Nickname, ulong ActorId);
public sealed record ConcertObservation(ulong Id, ulong HostCharacterId, IReadOnlyList<ulong> OrderedCharacterIds)
{ public IReadOnlyList<GameIdentity> Members { get; init; } = []; }
public sealed record MusicObservation(ulong ConcertId, ulong PerformerCharacterId, ulong HostCharacterId,
    string Title, bool IsLoop, IReadOnlyList<int> SelectedChannels, string? ScoreSha256,
    double? WholeScoreDurationSeconds, double? SelectedDurationSeconds, string? Issue)
{
    // Network receipt time is not the client's replicated playback clock.
    public double? GameStartLocalMs => null;
    public double? EstimatedStartLocalMs { get; init; }
    public bool AutomaticStopAvailable => EstimatedStartLocalMs is not null && WholeScoreDurationSeconds is > 0 and <= 86400 && SelectedDurationSeconds is not null;
    public string TimingSource => "tw-receive-offset-v1";
}
public sealed record GameObservation(string Generation, long Sequence, GameIdentity? Identity, ConcertObservation? Concert, string Detail)
{ public MusicObservation? Music { get; init; } }

/// <summary>Wire facts tied to the fingerprinted TW build; never loads game code.</summary>
public static class TwGameProtocol
{
    public const string AssemblySha256 = "1675c2131534d0ab2fc053af8a2e394d326ba7d22eff0019734be96b1808678f";
    public const string MetadataSha256 = "3f6827a41d26ab50ef0051b881cb2c6dcab0c12aab5e95cc7da0071fae63a847";
    public const int MaximumBody = 4 * 1024 * 1024;
    private static readonly JsonDocument Schema = JsonDocument.Parse(typeof(TwGameProtocol).Assembly.GetManifestResourceStream("MabiSync.TwIdentitySchema")!);
    public static IReadOnlySet<int> KnownProtocols { get; } = ReadProtocols();
    private static HashSet<int> ReadProtocols()
    {
        using var doc = JsonDocument.Parse(typeof(TwGameProtocol).Assembly.GetManifestResourceStream("MabiSync.TwProtocols")!);
        return doc.RootElement.EnumerateObject().Select(p => int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
    }
    public static byte[] Expand(ReadOnlySpan<byte> body, byte flag)
    {
        if (body.Length > MaximumBody || flag > 1) throw new InvalidDataException("Unsupported frame extent/encoding");
        if (flag == 0) return body.ToArray();
        using var decoder = new BrotliDecoder();
        var output = new byte[MaximumBody];
        var result = decoder.Decompress(body, output, out var consumed, out var written);
        if (result != System.Buffers.OperationStatus.Done || consumed != body.Length) throw new InvalidDataException("Invalid or oversized Brotli frame");
        return output.AsSpan(0, written).ToArray();
    }
    public static GameIdentity DecodeCharacter(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new WireReader(body.ToArray());
            var character = (Dictionary<string, object?>)reader.Value("Shared.Protocols.Replications.GroupReplicatedDatas.CharacterPublicReplicatedData")!;
            reader.Finish();
            var identity = (Dictionary<string, object?>)character["CharacterIdentity"]!;
            var id = (Dictionary<string, object?>)identity["CharacterId"]!;
            var realm = (Dictionary<string, object?>)identity["RealmId"]!;
            return new(Convert.ToUInt64(id["int64Value"]), Convert.ToInt32(realm["rawValue"]), (string)identity["Name"]!, Convert.ToUInt64(character["Id"]));
        }
        catch (Exception error) when (error is not InvalidDataException && error is OverflowException or InvalidCastException or KeyNotFoundException or DecoderFallbackException)
        { throw new InvalidDataException("Invalid character identity", error); }
    }
    internal sealed class WireReader(byte[] bytes)
    {
        private int _at, _elements;
        private static readonly Encoding Utf16 = new UnicodeEncoding(false, false, true);
        public int Remaining => bytes.Length - _at;
        public ReadOnlySpan<byte> Take(int length)
        { if (length < 0 || length > Remaining) throw new InvalidDataException("Truncated field"); var value = bytes.AsSpan(_at, length); _at += length; return value; }
        public int Int() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public ulong ULong() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        public bool Bool() => Take(1)[0] switch { 0 => false, 1 => true, _ => throw new InvalidDataException("Invalid bool") };
        public int Count(int maximum = 4096)
        { var count = Int(); if (count < 0 || count > maximum || (_elements += count) > 65536) throw new InvalidDataException("Invalid collection size"); return count; }
        public string Text()
        { var length = Int(); if (length < 0 || (length & 1) != 0) throw new InvalidDataException("Invalid UTF16 length"); return Utf16.GetString(Take(length)); }
        public void Finish() { if (Remaining != 0) throw new InvalidDataException("Trailing wire bytes"); }
        public object? Value(string name, int depth = 0)
        {
            if (depth > 48) throw new InvalidDataException("Schema recursion limit");
            switch (name)
            {
                case "int8": return unchecked((sbyte)Take(1)[0]); case "uint8": return Take(1)[0];
                case "int16": return BinaryPrimitives.ReadInt16LittleEndian(Take(2));
                case "uint16": case "char": return BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
                case "int32": return Int(); case "uint32": return BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
                case "int64": return BinaryPrimitives.ReadInt64LittleEndian(Take(8));
                case "uint64": case "Shared.ChannelObjects.ChannelObjectId": return ULong();
                case "float32": return BitConverter.Int32BitsToSingle(Int());
                case "float64": return BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(Take(8)));
                case "bool": return Bool(); case "string": return Text();
                case "object": case "System.Object": case "System.ValueType": case "System.Enum": return new Dictionary<string, object?>();
                case "Shared.Symbols.ActorModelId": case "Shared.Symbols.ModelPartId": case "Shared.Symbols.MountPartId": return new Dictionary<string, object?> { ["Id"] = Int() };
                case "Silvervine.ManualMemory.UniqueString": return new Dictionary<string, object?> { ["actualString"] = Text() };
                case "Shared.Protocols.ProtocolDatas.ReplicatedDatas.ReplicatedTime": return new Dictionary<string, object?> { ["offsetInSeconds"] = BitConverter.Int32BitsToSingle(Int()) };
            }
            if (name.EndsWith("[]", StringComparison.Ordinal))
            { var count = Count(); return Enumerable.Range(0, count).Select(_ => Value(name[..^2], depth + 1)).ToArray(); }
            var opening = name.IndexOf('<');
            if (opening >= 0 && name.EndsWith('>'))
            {
                var prefix = name[..opening]; var args = Arguments(name[(opening + 1)..^1]);
                if (prefix.EndsWith("Nullable`1", StringComparison.Ordinal) && args.Length == 1)
                {
                    var flag = Bool();
                    if (args[0] == "int64") return flag ? Value(args[0], depth + 1) : null;
                    if (Schema.RootElement.TryGetProperty(args[0], out var item) && item.GetProperty("parent").GetString() == "System.Enum") return flag ? null : Value(args[0], depth + 1);
                    throw new InvalidDataException("Unverified nullable codec");
                }
                if (args.Length == 1 && (prefix.EndsWith("List`1", StringComparison.Ordinal) || prefix.EndsWith("HashSet`1", StringComparison.Ordinal)))
                { var count = Count(); return Enumerable.Range(0, count).Select(_ => Value(args[0], depth + 1)).ToArray(); }
                if (args.Length == 2 && (prefix.EndsWith("Dictionary`2", StringComparison.Ordinal)))
                { var count = Count(); return Enumerable.Range(0, count).Select(_ => new[] { Value(args[0], depth + 1), Value(args[1], depth + 1) }).ToArray(); }
                if (args.Length == 2 && (prefix.EndsWith("KeyValuePair`2", StringComparison.Ordinal) || prefix.EndsWith("ValueTuple`2", StringComparison.Ordinal))) return args.Select(a => Value(a, depth + 1)).ToArray();
                throw new InvalidDataException("Unsupported generic wire type");
            }
            if (!Schema.RootElement.TryGetProperty(name, out var record)) throw new InvalidDataException("Unsupported wire type " + name);
            var parent = record.GetProperty("parent").GetString()!;
            if (parent == "System.Enum") return Value(record.GetProperty("fields").EnumerateArray().First(f => f.GetProperty("name").GetString() == "value__").GetProperty("type").GetString()!, depth + 1);
            var value = (Dictionary<string, object?>)Value(parent, depth + 1)!;
            foreach (var field in record.GetProperty("fields").EnumerateArray()) value[field.GetProperty("name").GetString()!] = Value(field.GetProperty("type").GetString()!, depth + 1);
            return value;
        }
        private static string[] Arguments(string value)
        {
            List<string> result = []; int depth = 0, start = 0;
            for (var i = 0; i < value.Length; i++)
            { if (value[i] == '<') depth++; else if (value[i] == '>') depth--; else if (value[i] == ',' && depth == 0) { result.Add(value[start..i]); start = i + 1; } }
            result.Add(value[start..]); return result.ToArray();
        }
    }
}
