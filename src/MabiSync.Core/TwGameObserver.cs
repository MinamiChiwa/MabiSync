namespace MabiSync.Core;

/// <summary>One instance per receive connection. Informational only; never issues input.</summary>
public sealed class TwGameObserver
{
    private readonly Dictionary<ulong, GameIdentity> _actors = [];
    private readonly Dictionary<ulong, ulong> _concerts = [], _playing = [];
    private sealed record Score(string Title, bool Loop, string WireHash, TwScoreTimeline Content, double? EstimatedStart);
    private readonly Dictionary<ulong, Score> _scores = [];
    private readonly Dictionary<ulong, int[]> _parts = [];
    private ulong? _personal;
    private bool _personalAwaitingChannel;
    private ulong[] _order = [];
    private string _generation = Guid.NewGuid().ToString("N");
    private long _sequence;
    public GameObservation Current { get; private set; }
    public event Action<GameObservation>? Changed;
    public TwGameObserver() => Current = new(_generation, ++_sequence, null, null, "等待识别角色昵称，请切换地图或重新登录角色。");
    public void Reset(string reason)
    {
        _actors.Clear(); _concerts.Clear(); _playing.Clear(); _scores.Clear(); _parts.Clear(); _personal = null; _personalAwaitingChannel = false; _order = [];
        _generation = Guid.NewGuid().ToString("N"); _sequence = 0; Publish(reason);
    }
    public void AcceptIdentity(GameIdentity identity)
    {
        if (identity.ActorId == 0 || identity.CharacterId == 0 || identity.RealmId < 0 || string.IsNullOrWhiteSpace(identity.Nickname) || identity.Nickname.Length > 64)
            throw new InvalidDataException("Invalid identity");
        if (_actors.Count >= 4096 && !_actors.ContainsKey(identity.ActorId)) Reset("已清理之前的角色信息，等待重新识别。");
        _actors[identity.ActorId] = identity; Publish();
    }
    public void AcceptPersonalId(ulong id)
    {
        if (id == 0) throw new InvalidDataException("Invalid local character ID");
        if (_personal is not null) Reset("角色信息已更新，等待重新识别。");
        _personal = id; _personalAwaitingChannel = true; Publish();
    }
    public void AcceptConcert(ulong id, ulong host)
    {
        if (id == 0 || host == 0) throw new InvalidDataException("Invalid concert identity");
        if (_concerts.Count >= 256 && !_concerts.ContainsKey(id))
        { var oldest = _concerts.Keys.First(); _concerts.Remove(oldest); _scores.Remove(oldest); }
        _concerts[id] = host; Publish();
    }
    public void Accept(int code, ReadOnlySpan<byte> bytes, double? receivedLocalMs = null)
    {
        var reader = new TwGameProtocol.WireReader(bytes.ToArray());
        switch (code)
        {
            case 20307:
                // Captured TW ordering is PersonalData -> EnteringChannel -> PublicData.
                // Carry only the pending initializer's ID, never old actor/concert state.
                var initializedId = _personalAwaitingChannel ? _personal : null;
                Reset("游戏正在切换频道，等待重新识别角色。"); _personal = initializedId; return;
            case 20047: AcceptIdentity(TwGameProtocol.DecodeCharacter(bytes)); return;
            case 20520:
                // Static BasePersonalReplicatedData first field, not whole PersonalData.
                AcceptPersonalId(reader.ULong()); return;
            case 20533:
                var count = reader.Count(128); var order = new ulong[count];
                for (var i = 0; i < count; i++) order[i] = reader.ULong();
                reader.Finish();
                if (order.Any(id => id == 0) || order.Distinct().Count() != count) throw new InvalidDataException("Duplicate/invalid ordered actor ID");
                _order = order; break;
            case 120412:
                var concert = reader.ULong(); var host = reader.ULong(); var title = reader.Text(); reader.Text();
                var score = reader.Take(reader.Int()).ToArray(); reader.Bool(); var loop = reader.Bool();
                var start = BitConverter.Int32BitsToSingle(reader.Int());
                if (!float.IsFinite(start)) throw new InvalidDataException("Invalid concert offset");
                reader.Finish();
                if (concert == 0 || host == 0) throw new InvalidDataException("Invalid concert identity");
                // Keep identity evidence when score content is corrupt. Bound only the
                // score cache; a refresh is not a game connection reset.
                if (_concerts.Count >= 256 && !_concerts.ContainsKey(concert))
                {
                    var oldest = _concerts.Keys.First(); _scores.Remove(oldest); _concerts.Remove(oldest);
                }
                var wireHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(score));
                title = string.Concat(title.Where(c => !char.IsControl(c))).Trim();
                if (title.Length > 512) title = title[..512];
                if (!_scores.TryGetValue(concert, out var cached) || cached.WireHash != wireHash || cached.Loop != loop)
                    cached = new(title, loop, wireHash, TwScoreTimeline.Decode(score, loop),
                        receivedLocalMs is { } received && double.IsFinite(received) ? received + start * 1000d : null);
                else cached = cached with { Title = title };
                _scores[concert] = cached; _concerts[concert] = host; break;
            case 120692:
                var actor = reader.ULong(); var playing = reader.ULong(); var channels = reader.Count(128);
                var parts = new int[channels];
                for (var i = 0; i < channels; i++) { var channel = reader.Int(); if (channel < 0 || channel >= 128) throw new InvalidDataException("Invalid instrument channel"); parts[i] = channel; }
                reader.Int(); reader.Bool(); reader.Finish();
                if (actor == 0 || playing == 0) throw new InvalidDataException("Invalid Play identity");
                if (_playing.Count >= 4096 && !_playing.ContainsKey(actor))
                { var oldest = _playing.Keys.First(); RemovePlay(oldest); }
                _playing[actor] = playing; _parts[actor] = parts; break;
            case 120693:
                actor = reader.ULong(); var stoppedConcert = reader.Remaining == 8 ? reader.ULong() : (ulong?)null; reader.Finish();
                if (stoppedConcert is null || _playing.GetValueOrDefault(actor) == stoppedConcert) RemovePlay(actor);
                break;
            case 120413:
                concert = reader.ULong(); reader.Finish(); _concerts.Remove(concert); _scores.Remove(concert);
                if (_order.Any(id => _playing.GetValueOrDefault(id) == concert)) _order = [];
                foreach (var id in _playing.Where(p => p.Value == concert).Select(p => p.Key).ToArray()) RemovePlay(id);
                break;
            case 20238:
                actor = reader.ULong(); reader.Finish();
                if (Current.Identity?.ActorId == actor) { Reset("角色已离开当前频道，等待重新识别。"); return; }
                _actors.Remove(actor); RemovePlay(actor); break;
            default: return;
        }
        Publish();
    }
    private void RemovePlay(ulong actor)
    {
        _playing.Remove(actor); _parts.Remove(actor);
        if (_order.Contains(actor)) _order = [];
    }
    private MusicObservation? LocalMusic(GameIdentity? local)
    {
        if (local is null || !_playing.TryGetValue(local.ActorId, out var id) || !_scores.TryGetValue(id, out var score) || !_concerts.TryGetValue(id, out var host)) return null;
        var selected = _parts.GetValueOrDefault(local.ActorId) ?? [];
        var timeline = score.Content.Timeline;
        var issue = score.Content.Issue;
        double? whole = null, subset = null;
        if (timeline is { TimingSupported: true })
        {
            whole = timeline.Tracks.Max(t => t.LastEventEndSeconds);
            if (selected.Length > 0 && selected.Distinct().Count() == selected.Length && selected.All(i => i < timeline.Tracks.Count))
                subset = selected.Max(i => timeline.Tracks[i].LastEventEndSeconds);
            else issue = "尚未确认本次演奏包含哪些音轨。";
            if (whole > 7 * 24 * 3600 || subset > 7 * 24 * 3600)
            { whole = subset = null; issue = "乐谱过长，暂时无法计算一轮结束时间。"; }
        }
        issue ??= score.EstimatedStart is null ? "乐谱时长已识别，等待识别本轮演奏的开始时间。" : "已根据抓包估算一轮结束时间。可用“本机时间微调”调整。";
        return new(id, local.CharacterId, host, score.Title, score.Loop, Array.AsReadOnly(selected.Distinct().ToArray()), score.Content.Sha256, whole, subset, issue)
            { EstimatedStartLocalMs = score.EstimatedStart };
    }
    private void Publish(string? detail = null)
    {
        var matches = _actors.Values.Where(a => a.CharacterId == _personal).ToArray();
        var local = matches.Length == 1 ? matches[0] : null;
        ConcertObservation? concert = null;
        if (local is not null && _order.Contains(local.ActorId) && _order.Length > 0 && _order.All(id => _actors.ContainsKey(id) && _playing.ContainsKey(id)))
        {
            var concerts = _order.Select(id => _playing[id]).Distinct().ToArray();
            var members = _order.Select(id => _actors[id]).ToArray();
            if (concerts.Length == 1 && _concerts.TryGetValue(concerts[0], out var host) && host == members[0].CharacterId && members.All(m => m.RealmId == local.RealmId) && members.Select(m => m.CharacterId).Distinct().Count() == members.Length)
                concert = new(concerts[0], host, Array.AsReadOnly(members.Select(m => m.CharacterId).ToArray())) { Members = Array.AsReadOnly(members) };
        }
        Current = new(_generation, ++_sequence, local, concert, detail ?? (local is null ? "请先切换地图或重新登录角色，识别昵称后即可核对加入顺序。" : concert is null ? "已识别角色昵称，等待识别合奏成员。" : "已更新合奏加入顺序。")) { Music = LocalMusic(local) };
        Changed?.Invoke(Current);
    }
}
