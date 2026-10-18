using System.Globalization;
using System.Windows.Threading;
using MabiSync.Core;
using MabiSync.Windows;

namespace MabiSync.App;

public partial class MainWindow
{
    private readonly GameObservationCapture _gameObservation = new();
    private readonly DispatcherTimer _observationTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private Task _observationSend = Task.CompletedTask;
    private string? _reportedObservation, _reportedMember;
    private double _observationSentAt = double.NegativeInfinity;
    private long _observationSequence;
    private string? _localEnsembleLayout;
    private bool _wasEnsembleReady;
    private int _observationQueued;
    private void InitializeObservation()
    {
        _observationTimer.Tick += (_, _) => ObservationTick(); _observationTimer.Start();
        _gameObservation.Changed+=CaptureObservationChanged;
    }
    private void CaptureObservationChanged(CaptureObservation state)
    {
        if(_closing || Interlocked.Exchange(ref _observationQueued,1)!=0)return;
        Dispatcher.BeginInvoke(()=>{
            Interlocked.Exchange(ref _observationQueued,0);
            if(!_closing){ObservationTick();RefreshStatus();}
        });
    }
    private void ObservationTick()
    {
        if (_closing) return;
        var state = _gameObservation.Current;
        ObservationStatusText.Text = state.Status;
        ObservationStatusText.Foreground = ColorBrush(state.Running && state.Game.Identity is not null ? "#86E1B2" : "#A8B4C7");
        if (state.Game.Identity is { } identity && NicknameBox.Text != identity.Nickname)
            NicknameBox.Text = identity.Nickname;
        if (!ViewConnected || !_client.SupportsGameObservation || ViewSession is not { } session || !_observationSend.IsCompleted) return;
        var performance = ViewRoom?.GetProperty("performance");
        var round = performance is { ValueKind: System.Text.Json.JsonValueKind.Object } p
            ? p.GetProperty("id").GetString() + ":" + p.GetProperty("phase").GetString() : "";
        // A new/committed round must not wait for the unchanged-capture heartbeat.
        var signature = state.Game.Generation + ":" + state.Game.Sequence + ":" + round + ":" + (_roomClock.Estimate is not null);
        if (_reportedObservation == signature && _reportedMember == session.MemberId && _clock.NowMs - _observationSentAt < 3000) return;
        _observationSend = ReportObservationAsync(state, signature, session.MemberId);
    }
    private async Task ReportObservationAsync(CaptureObservation state, string signature, string memberId)
    {
        try
        {
            var local = state.Running ? state.Game.Identity : null;
            var concert = local is not null ? state.Game.Concert : null;
            var music = local is not null ? state.Game.Music : null;
            var performance = ViewRoom?.GetProperty("performance");
            var sessionId = performance is { ValueKind: System.Text.Json.JsonValueKind.Object } p && p.GetProperty("phase").GetString() is not ("cancelled" or "stopped") ? p.GetProperty("id").GetString() : null;
            var estimatedStart = music is { AutomaticStopAvailable: true, EstimatedStartLocalMs: { } start } && _roomClock.Estimate is { } clock
                ? start + clock.OffsetMs : (double?)null;
            await _client.SendAsync(new { type = "game-observation", generation = state.Game.Generation,
                sessionId,
                sequence = ++_observationSequence, status = local is null ? "unavailable" : "identified",
                identity = local is null ? null : new { characterId = local.CharacterId.ToString(CultureInfo.InvariantCulture), realmId = local.RealmId, nickname = local.Nickname },
                concert = concert is null ? null : new { id = concert.Id.ToString(CultureInfo.InvariantCulture), hostCharacterId = concert.HostCharacterId.ToString(CultureInfo.InvariantCulture),
                    orderedCharacterIds = concert.OrderedCharacterIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray(),
                    members = concert.Members.Select(m => new { characterId = m.CharacterId.ToString(CultureInfo.InvariantCulture), realmId = m.RealmId, nickname = m.Nickname }).ToArray() },
                music = music is null ? null : new { concertId = music.ConcertId.ToString(CultureInfo.InvariantCulture),
                    performerCharacterId = music.PerformerCharacterId.ToString(CultureInfo.InvariantCulture), hostCharacterId = music.HostCharacterId.ToString(CultureInfo.InvariantCulture),
                    title = music.Title, isLoop = music.IsLoop, selectedChannels = music.SelectedChannels,
                    scoreSha256 = music.ScoreSha256, wholeScoreDurationSeconds = music.WholeScoreDurationSeconds,
                    selectedDurationSeconds = music.SelectedDurationSeconds, issue = music.Issue,
                    timingSource = music.TimingSource, estimatedStartServerMs = estimatedStart } });
            _reportedObservation = signature; _reportedMember = memberId; _observationSentAt = _clock.NowMs;
        }
        catch (Exception error) { if (!_closing && ViewConnected) MessageText.Text = "抓包识别结果暂未更新到房间：" + Friendly(error.Message); }
    }
    private void SyncEnsembleInputPermission()
    {
        var self = _members.FirstOrDefault(m => m.Id == ViewSession?.MemberId);
        var signature = self is null ? null : $"{self.Id}/{self.Group}/{self.Order}/{self.EnsembleRole}";
        if (SelectedMode == "ensemble" && !RoundActive &&
            ((_localEnsembleLayout is not null && signature != _localEnsembleLayout) || (_wasEnsembleReady && self?.Ready != true)))
            InvalidateReadiness();
        if(signature!=_localEnsembleLayout)RebuildGameProfile();
        _localEnsembleLayout = signature; _wasEnsembleReady = self?.Ready == true;
    }
}
