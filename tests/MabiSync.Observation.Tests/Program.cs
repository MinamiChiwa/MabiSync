using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MabiSync.Core;

var tests = new List<(string, Action)>();
void Add(string name, Action test) => tests.Add((name, test));
void Check(bool condition, string detail) { if (!condition) throw new Exception(detail); }
byte[] Id(ulong value) => BitConverter.GetBytes(value);
byte[] Join(params ulong[] values) => BitConverter.GetBytes(values.Length).Concat(values.SelectMany(Id)).ToArray();
byte[] Play(ulong actor, ulong concert) => Id(actor).Concat(Id(concert)).Concat(BitConverter.GetBytes(0)).Concat(BitConverter.GetBytes(1)).Append((byte)1).ToArray();
byte[] Concert(ulong concert, ulong host, string mml, string title = "Fixture score", bool corrupt = false)
{
    using var data = new MemoryStream();
    // The four observed envelope bytes are not an expanded-length allocation hint.
    data.Write(new byte[] { 255, 255, 255, 127 });
    using (var compression = new BrotliStream(data, CompressionLevel.Fastest, true)) compression.Write(Encoding.UTF8.GetBytes(mml));
    var blob = corrupt ? new byte[] { 1, 2, 3, 4, 5 } : data.ToArray();
    using var wire = new MemoryStream(); using var writer = new BinaryWriter(wire);
    void Text(string text) { var bytes = Encoding.Unicode.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes); }
    writer.Write(concert); writer.Write(host); Text(title); Text("Fixture author"); writer.Write(blob.Length); writer.Write(blob);
    writer.Write(false); writer.Write(true); writer.Write(1.5f);
    return wire.ToArray();
}
byte[] PlayParts(ulong actor, ulong concert, params int[] channels) => Id(actor).Concat(Id(concert)).Concat(BitConverter.GetBytes(channels.Length)).Concat(channels.SelectMany(BitConverter.GetBytes)).Concat(BitConverter.GetBytes(1)).Append((byte)1).ToArray();

Add("Complete synthetic character snapshot reads exact identity and rejects truncated or trailing bytes", () =>
{
    var data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "tw-character-synthetic.bin"));
    var identity = TwGameProtocol.DecodeCharacter(data);
    Check(identity == new GameIdentity(101, 3, "Fixture玩家", 11), "Wrong identity fields");
    foreach (var bad in new[] { data[..^1], data.Concat(new byte[] { 0 }).ToArray() })
    { try { TwGameProtocol.DecodeCharacter(bad); throw new Exception("Invalid snapshot accepted"); } catch (InvalidDataException) { } }
});
Add("Join array cannot identify a concert until local and participant evidence agree", () =>
{
    var observer = new TwGameObserver();
    observer.AcceptIdentity(new(101, 3, "Local", 11)); observer.AcceptIdentity(new(102, 3, "Other", 22));
    observer.AcceptPersonalId(101); observer.AcceptConcert(7, 101);
    observer.Accept(120692, Play(11, 7)); observer.Accept(20533, Join(11, 22));
    Check(observer.Current.Identity?.Nickname == "Local" && observer.Current.Concert is null, "Premature confirmation");
    observer.Accept(120692, Play(22, 7));
    Check(observer.Current.Concert?.OrderedCharacterIds.SequenceEqual(new ulong[] { 101, 102 }) == true, "Order lost");
    observer.Accept(120693, Id(22)); Check(observer.Current.Concert is null, "Stopped actor kept confirmation");
    var generation = observer.Current.Generation; observer.Reset("channel changed");
    Check(observer.Current.Identity is null && observer.Current.Generation != generation, "Old identity survived epoch");
});
Add("Channel entry follows personal initialization and retains only its new local ID", () =>
{
    var observer = new TwGameObserver(); observer.AcceptPersonalId(101);
    observer.Accept(20307, []); observer.AcceptIdentity(new(101, 3, "NewChannel", 55));
    Check(observer.Current.Identity?.ActorId == 55, "EnteringChannel discarded preceding PersonalData ID");
    observer.Accept(20307, []);
    Check(observer.Current.Identity is null, "Channel entry reused an old identity without initialization");
});
Add("Wrong host, foreign concert and duplicate identity never confirm membership", () =>
{
    var observer = new TwGameObserver(); observer.AcceptPersonalId(101);
    observer.AcceptIdentity(new(101, 3, "Same", 11)); observer.AcceptIdentity(new(102, 3, "Same", 22));
    observer.AcceptConcert(7, 102); observer.Accept(120692, Play(11, 7)); observer.Accept(120692, Play(22, 7));
    observer.Accept(20533, Join(11, 22)); Check(observer.Current.Concert is null, "Wrong host accepted");
    observer.AcceptConcert(7, 101); Check(observer.Current.Concert is not null, "Valid group failed");
    observer.Accept(120692, Play(22, 8)); Check(observer.Current.Concert is null, "Foreign concert accepted");
    observer.AcceptIdentity(new(101, 3, "Same", 33)); Check(observer.Current.Identity is null, "Ambiguous identity accepted");
});
Add("TCP segmentation, out-of-order, retransmission, wrap and gaps preserve exact frame boundaries", () =>
{
    var bodies = new List<byte[]>(); var resets = 0;
    var stream = new GameTcpStream((code, bytes) => bodies.Add(bytes), _ => resets++);
    byte[] Frame(ulong n) { var body = Id(n); return BitConverter.GetBytes(120693).Concat(BitConverter.GetBytes(body.Length)).Append((byte)0).Concat(body).ToArray(); }
    var data = Frame(11).Concat(Frame(22)).Concat(Frame(33)).ToArray();
    stream.Feed(0xfffffff0, data[..10], 0);
    stream.Feed(14, data[30..], 2);
    stream.Feed(0xfffffffa, data[10..30], 3);
    stream.Feed(0xfffffffa, data[10..30], 4);
    Check(bodies.Count == 3 && BitConverter.ToUInt64(bodies[1]) == 22, "Split/out-of-order corrupted frames");
    stream.Feed(1000, data, 10); stream.CheckGap(3000);
    Check(resets > 0, "TCP gap did not invalidate observer");
});
Add("Truncated packets and IP fragments do not create complete game payloads", () =>
{
    var raw=new byte[45]; raw[0]=0x45; raw[9]=6; raw[12]=127; raw[15]=1; raw[16]=127; raw[19]=2;
    BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(2),45); raw[32]=0x50; raw[40]=9;
    var packet=GamePacket.Read(101,raw); Check(packet?.Payload.Length==5,"Valid packet rejected");
    Check(GamePacket.Read(101,raw[..^1]) is null,"Truncated capture accepted");
    raw[6]=0x20; Check(GamePacket.Read(101,raw) is null,"Fragment accepted");
});
Add("Conflicting retransmits and malformed joins clear the observation instead of keeping valid evidence", () =>
{
    var reset=0; var stream=new GameTcpStream((_,_)=>{},_=>reset++);
    stream.Feed(100,new byte[] {1,2,3},0); stream.Feed(100,new byte[] {1,9,3},1);
    Check(reset==1,"Conflicting retransmit was ignored");
    var observer=new TwGameObserver();
    foreach(var bad in new[]{Join(11,11),Join(0),Join(11)[..^1]})
    {try{observer.Accept(20533,bad);throw new Exception("Invalid member snapshot accepted");}catch(InvalidDataException){}}
});
Add("Captured music associates the local actor with its score and keeps full and selected durations distinct", () =>
{
    var observer = new TwGameObserver(); observer.AcceptPersonalId(101); observer.AcceptIdentity(new(101, 3, "Local", 11));
    observer.Accept(120412, Concert(7, 101, "MML@T120C4,C2;"));
    observer.Accept(120692, PlayParts(22, 7, 1));
    Check(observer.Current.Music is null, "A nearby player's play selected the local score");
    observer.Accept(120692, PlayParts(11, 7, 0));
    var music = observer.Current.Music;
    Check(music is { ConcertId: 7, PerformerCharacterId: 101, Title: "Fixture score", IsLoop: true }, "Local score metadata missing");
    Check(music!.SelectedChannels.SequenceEqual(new[] { 0 }) && music.SelectedDurationSeconds == .5 && music.WholeScoreDurationSeconds == 1, "Actual selected channels lost or guessed");
    Check(!music.AutomaticStopAvailable && music.GameStartLocalMs is null, "A receive event was presented as verified onset");
    Check(music.ScoreSha256 is { Length: 64 }, "Score fingerprint missing");
    observer.Accept(120693, Id(11)); Check(observer.Current.Music is null, "Stopped local music remained active");
});
Add("Unreadable score or invalid selected channels do not remove valid local identity or fabricate a duration", () =>
{
    var observer = new TwGameObserver(); observer.AcceptPersonalId(101); observer.AcceptIdentity(new(101, 3, "Local", 11));
    observer.Accept(120412, Concert(7, 101, "MML@T120C4;", corrupt: true)); observer.Accept(120692, PlayParts(11, 7, 0));
    Check(observer.Current.Identity?.CharacterId == 101 && observer.Current.Music?.WholeScoreDurationSeconds is null, "Bad score erased identity or created timing");
    Check(observer.Current.Music?.Issue is not null, "Corrupt score has no explanation");
    observer.Accept(120412, Concert(7, 101, "MML@T120C4;")); observer.Accept(120692, PlayParts(11, 7, 2));
    Check(observer.Current.Identity is not null && observer.Current.Music?.SelectedDurationSeconds is null && observer.Current.Music?.Issue is not null, "Out-of-range voice supplied a valid selected duration");
    observer.Reset("new connection"); Check(observer.Current.Music is null, "Music crossed a connection generation");
});
Add("Score arrival after play resolves the same performance and duplicates never resurrect a removed score", () =>
{
    var observer = new TwGameObserver(); observer.AcceptPersonalId(101); observer.AcceptIdentity(new(101, 3, "Local", 11));
    observer.Accept(120692, PlayParts(11, 7, 0)); Check(observer.Current.Music is null, "Missing score was invented");
    observer.Accept(120412, Concert(7, 101, "MML@T120C4;"));
    Check(observer.Current.Music?.WholeScoreDurationSeconds == .5, "Late score did not resolve");
    observer.Accept(120413, Id(7)); observer.Accept(120412, Concert(7, 101, "MML@T120C4;"));
    Check(observer.Current.Music is null, "An old score Add replayed a removed Play");
});
Add("TW timing applies shared tempo and excludes the final rest duration without changing standard MML", () =>
{
    var observer = new TwGameObserver(); observer.AcceptPersonalId(101); observer.AcceptIdentity(new(101, 3, "Local", 11));
    observer.Accept(120412, Concert(7, 101, "MML@T60C4R4R4,C1;")); observer.Accept(120692, PlayParts(11, 7, 0));
    Check(observer.Current.Music?.SelectedDurationSeconds == 2 && observer.Current.Music.WholeScoreDurationSeconds == 4, "TW last-event or shared tempo timing differs");
    Check(MmlTimeline.Parse("T60C4R4R4").WholeScoreSeconds == 3, "TW convention changed the standard parser");
    observer.Accept(120692, PlayParts(11, 8, 0));
    observer.Accept(120693, Id(11).Concat(Id(7)).ToArray());
    observer.Accept(120412, Concert(8, 101, "MML@T60C1;"));
    Check(observer.Current.Music?.ConcertId == 8, "A delayed old stop removed a new performance");
});
Add("Playback estimate uses first Add receipt and never relabels it as actual game time", () =>
{
    var observer = new TwGameObserver(); observer.AcceptPersonalId(101); observer.AcceptIdentity(new(101, 3, "Local", 11));
    var body = Concert(7, 101, "MML@T120C4;");
    observer.Accept(120412, body, 1000); observer.Accept(120692, PlayParts(11, 7, 0));
    Check(observer.Current.Music?.EstimatedStartLocalMs == 2500 && observer.Current.Music.AutomaticStopAvailable, "Missing receipt-based estimate");
    Check(observer.Current.Music!.GameStartLocalMs is null, "Estimate claimed to be actual game time");
    observer.Accept(120412, body, 9000);
    Check(observer.Current.Music?.EstimatedStartLocalMs == 2500, "Duplicate Add moved the first-cycle target");
    observer.Accept(120693, Id(11)); Check(observer.Current.Music is null, "Stopped estimate survived");
});
Add("Split frames retain packet receipt times without using later parser wake time", () =>
{
    GameTcpStream? stream = null; double? time = null;
    stream = new GameTcpStream((_, _) => time = stream!.FrameReceivedLocalMs, _ => {});
    byte[] Frame(ulong n) => BitConverter.GetBytes(120693).Concat(BitConverter.GetBytes(8)).Append((byte)0).Concat(Id(n)).ToArray();
    var wire = Frame(11).Concat(Frame(22)).ToArray();
    stream.Feed(100, wire[..20], 1000); stream.Feed(120, wire[20..], 1200);
    Check(time == 1200, "Packet receipt timestamp lost");
});
var failures = 0;
if (args.Length == 2 && args[0] == "--score-body")
{
    // Content verification only. Replace identifiers with fixtures; this mode
    // does not claim to identify the player in a capture lacking initialization.
    var body = File.ReadAllBytes(args[1]);
    BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(0, 8), 7);
    BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8, 8), 101);
    var observer = new TwGameObserver(); observer.AcceptPersonalId(101); observer.AcceptIdentity(new(101, 3, "Fixture", 11));
    observer.Accept(120412, body); observer.Accept(120692, PlayParts(11, 7, 0, 1));
    var score = observer.Current.Music!;
    Console.WriteLine(JsonSerializer.Serialize(new { scope = "content-only-fixture-identity", score.ScoreSha256,
        score.WholeScoreDurationSeconds, score.SelectedDurationSeconds, score.AutomaticStopAvailable, score.Issue }));
    return score.WholeScoreDurationSeconds is not null ? 0 : 1;
}
if (args.Length == 2 && args[0] == "--replay")
{
    using var replay = JsonDocument.Parse(File.ReadAllBytes(args[1]));
    var identities = 0; var errors = 0; HashSet<string> memberships = [], scores = []; List<object> changes = [];
    foreach (var item in replay.RootElement.EnumerateArray())
    {
        var observer = new TwGameObserver();
        observer.Changed += s => { changes.Add(new { stream = item.GetProperty("stream").GetString(), state = s }); if (s.Concert is { } c) memberships.Add(c.Id + ":" + string.Join(',', c.OrderedCharacterIds)); if (s.Music?.ScoreSha256 is { } hash) scores.Add(hash); };
        var stream = new GameTcpStream((code, body) => { observer.Accept(code, body); if (code == 20047) identities++; }, reason => { errors++; observer.Reset(reason); });
        var bytes = Convert.FromBase64String(item.GetProperty("data").GetString()!);
        for (var offset = 0; offset < bytes.Length; offset += 137)
            stream.Feed((uint)offset, bytes.AsSpan(offset, Math.Min(137, bytes.Length - offset)), offset);
    }
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(args[1])!, "dotnet-observer-private.json"), JsonSerializer.Serialize(changes));
    Console.WriteLine(JsonSerializer.Serialize(new { completePublicSnapshots = identities, confirmedMemberships = memberships.Count, localScores = scores.Count, errors }));
    return errors == 0 ? 0 : 1;
}
foreach (var (name, test) in tests) try { test(); Console.WriteLine("PASS " + name); } catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error); }
Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed");
return failures == 0 ? 0 : 1;
