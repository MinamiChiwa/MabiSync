using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MabiSync.Core;

namespace MabiSync.App;

public partial class MainWindow
{
    private string? _musicDurationError, _consumedMusicRound;
    private long _musicDurationMs = 30000;
    private long _musicTailHoldMs = 1000;
    private string? _musicTailHoldError;
    private readonly Dictionary<string, object> _roundTimingDiagnostics = [];
    private void RememberRoundTiming(JsonElement? performance)
    {
        if(performance is not {ValueKind:JsonValueKind.Object} p)return;
        var id=p.GetProperty("id").GetString()!;
        var start=p.GetProperty("startAction").GetProperty("targetServerMs").GetDouble();
        double? end=null, duration=null, stop=null;
        if(p.TryGetProperty("cycleScore",out var score) && score.ValueKind==JsonValueKind.Object){
            if(score.TryGetProperty("estimatedEndServerMs",out var e) && e.TryGetDouble(out var endpoint))end=endpoint;
            if(score.TryGetProperty("durationSeconds",out var d) && d.TryGetDouble(out var seconds))duration=seconds;
        }
        if(p.TryGetProperty("stopAction",out var action) && action.ValueKind==JsonValueKind.Object)stop=action.GetProperty("targetServerMs").GetDouble();
        _roundTimingDiagnostics[id]=new {
            mode=p.GetProperty("mode").GetString(),phase=p.GetProperty("phase").GetString(),
            stopRule=p.TryGetProperty("stopRule",out var rule)?rule.GetString():null,
            tailHoldMs=TailHoldMilliseconds(p), scoreDurationSeconds=duration,
            estimatedOnsetAfterScheduledStartMs=end-duration*1000-start,
            scoreEndAfterScheduledStartMs=end-start,stopAfterScheduledStartMs=stop-start,
            stopTimingSource=p.TryGetProperty("stopTimingSource",out var source)?source.GetString():null,
            cycleStopStatus=p.TryGetProperty("cycleStopStatus",out var status)?status.GetString():null
        };
        while(_roundTimingDiagnostics.Count>8)_roundTimingDiagnostics.Remove(_roundTimingDiagnostics.Keys.First());
    }
    private void MusicTailChanged(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady || _closing) return;
        try { _musicTailHoldMs = MusicTailText.ParseMilliseconds(TailHoldBox.Text); _musicTailHoldError = null; SaveSettings(); }
        catch (FormatException error) { _musicTailHoldError = error.Message; }
        Refresh();
    }
    private long PrepareTailHold()
    {
        var milliseconds=MusicTailText.ParseMilliseconds(TailHoldBox.Text);
        if(milliseconds>0 && !_client.SupportsFirstCycleTail)
            throw new InvalidOperationException("当前房间服务器尚未支持尾音保留，请更新房间服务后重建房间。临时测试可使用新版工具的局域网或 Cloudflare 建房。");
        return milliseconds;
    }
    private static long TailHoldMilliseconds(JsonElement performance) =>
        performance.TryGetProperty("tailHoldMs",out var tail) && tail.TryGetInt64(out var ms) && ms is >=0 and <=10000 ? ms : 0;
    private double StopRemainingMilliseconds(JsonElement stop) =>
        _scheduler.LocalDeadline(stop.GetProperty("id").GetString()!) is { } deadline ? deadline-_clock.NowMs :
        stop.GetProperty("targetServerMs").GetDouble()+TimingOffsetMs-(_roomClock.ServerNowMs??0);
    private bool HoldingTail(JsonElement performance) => TailHoldMilliseconds(performance)>0 &&
        performance.TryGetProperty("cycleScore",out var score) && score.ValueKind==JsonValueKind.Object &&
        score.TryGetProperty("estimatedEndServerMs",out var end) && end.ValueKind==JsonValueKind.Number &&
        _roomClock.ServerNowMs is { } now && now>=end.GetDouble();
    private void MusicDurationChanged(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady || _closing) return;
        try { _musicDurationMs = MusicDurationText.ParseMilliseconds(DurationBox.Text); _musicDurationError = null; SaveSettings(); }
        catch (FormatException error) { _musicDurationError = error.Message; }
        Refresh();
    }
    private async void ReportLeaderClick(object sender, RoutedEventArgs e) => await Run((Button)sender, async () =>
    {
        if (ViewRoom?.GetProperty("performance") is not { ValueKind: JsonValueKind.Object } p) throw new InvalidOperationException("当前没有正在进行的演奏。");
        await _client.SendAsync(new { type = "leader-status", sessionId = p.GetProperty("id").GetString(), status = "interrupted", evidenceSource = "player-report" });
        MessageText.Text = "已通知房主选择全员停止或继续。";
    });
    private void RefreshMusicStatus(JsonElement? performance)
    {
        RememberRoundTiming(performance);
        LeaderPurposeText.Text=SelectedStopRule switch {
            MusicStopRule.FirstCycle=>"按总指挥的乐谱估算一轮终点，保留尾音后全员停止。尾音保留由房主设置；总指挥提前结束时会提醒房主。",
            MusicStopRule.Duration=>"显示总指挥的乐谱；总指挥提前结束时可通知房主。全员按指定时长停止。",
            _=>"显示总指挥的乐谱；总指挥提前结束时可通知房主。何时全员停止由房主决定。"
        };
        var music = SelectedMode != "emote";
        MusicStatusText.Visibility = music && performance is {ValueKind:JsonValueKind.Object} ? Visibility.Visible : Visibility.Collapsed;
        RefreshScoreObservation(music, performance);
        ReportLeaderButton.Visibility = ContinueButton.Visibility = Visibility.Collapsed;
        if (!music) return;
        var host = ViewSession is { } self && ViewRoom?.GetProperty("hostId").GetString() == self.MemberId;
        var active = performance is { ValueKind: JsonValueKind.Object } p && p.GetProperty("phase").GetString() is not ("cancelled" or "stopped");
        if (performance is not { ValueKind: JsonValueKind.Object } value || value.GetProperty("mode").GetString() == "emote")
        { MusicStatusText.Text = host ? SelectedStopRule switch {
            MusicStopRule.Duration => _musicDurationError ?? $"结束方式：演奏 {_musicDurationMs / 1000m:0.###} 秒后全员停止。",
            MusicStopRule.FirstCycle => "总指挥需开启抓包识别。工具根据乐谱和开始时间估算一轮结束时间，届时全员停止。",
            _ => "结束方式：房主手动全员停止。" } : "总指挥与结束方式由房主设置。"; return; }
        if (!active && SelectedStopRule == MusicStopRule.Duration && _musicDurationError is not null) { MusicStatusText.Text = _musicDurationError; return; }
        var phase = value.GetProperty("phase").GetString();
        var leaderId = value.GetProperty("leaderId").GetString();
        var leader = _members.FirstOrDefault(m => m.Id == leaderId)?.Nickname ?? "未指定";
        var now = _roomClock.ServerNowMs;
        var startReached = now is { } time && time >= value.GetProperty("startAction").GetProperty("targetServerMs").GetDouble() + TimingOffsetMs;
        var rules = MusicRoundActions.For(value.GetProperty("mode").GetString()!, phase, host, ViewSession?.MemberId == leaderId, startReached);
        ReportLeaderButton.Visibility = rules.CanReportInterruption ? Visibility.Visible : Visibility.Collapsed;
        ReportLeaderButton.IsEnabled = rules.CanReportInterruption && ViewConnected;
        ContinueButton.Visibility = phase == "interrupted" && host ? Visibility.Visible : Visibility.Collapsed;
        ContinueButton.IsEnabled = rules.CanContinue && ViewConnected;
        var hasStop = value.TryGetProperty("stopAction", out var stop) && stop.ValueKind == JsonValueKind.Object;
        var automatic = value.TryGetProperty("stopRule",out var rule) && rule.GetString()=="first-cycle";
        var committed = !value.TryGetProperty("stopCommitted",out var sc) || sc.GetBoolean();
        ContinueButton.Content = hasStop && committed ? "继续，按原计划停止" : "继续，稍后手动停止";
        var ending = hasStop
            ? !committed ? "正在确认各成员能否停止演奏" : $"{(automatic && HoldingTail(value)?"尾音保留 · ":"")}距离本机停止还有 {Math.Max(0, StopRemainingMilliseconds(stop)/1000):0.0} 秒（{(automatic?"总指挥一轮 · 抓包估计":"指定时长")}）"
            : automatic ? CycleWaitingText(value) : "由房主手动全员停止";
        var detail = value.GetProperty("interruption");
        var interruption = detail.ValueKind == JsonValueKind.Object ? InterruptionText(detail) : "";
        var failures = value.GetProperty("receipts").EnumerateArray().Count(r => r.GetProperty("status").GetString() is "failed" or "missed");
        var warning = failures > 0 ? $"有 {failures} 次操作未执行成功，请查看“执行记录与诊断”并检查游戏。" : "";
        MusicStatusText.Text = phase switch
        {
            "interrupted" => $"总指挥：{leader} · {interruption}。{ending}。由房主决定全停或继续。",
            "cancelled" => $"本轮已取消：{interruption}。下一轮请重新准备并就绪。",
            "stopped" => warning + "本轮已结束，请确认游戏中已停止演奏。下一轮请重新就绪。",
            _ => $"总指挥：{leader} · {ending}。"
        };
    }
    private static string CycleWaitingText(JsonElement performance) => performance.TryGetProperty("cycleStopStatus",out var status) ? status.GetString() switch
    {
        "too-late" => "识别乐谱时已接近一轮结束，来不及安排自动停止，请房主手动全员停止",
        "participant-not-ready" => "有成员尚未设置好停止操作，请房主手动全员停止",
        "prepare-timeout" => "未能及时确认所有成员的停止设置，请房主手动全员停止",
        "interrupted-before-stop-commit" => "尚未安排统一停止，请房主选择继续或全员停止",
        _ => "等待识别总指挥本轮的乐谱和开始时间；房主可随时全员停止"
    } : "等待识别总指挥的乐谱和开始时间";
    private void RefreshScoreObservation(bool visible, JsonElement? performance)
    {
        ScoreCard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ScoreObservationText.Foreground=ColorBrush("#BECBDD");
        ScoreObservationText.ToolTip="总指挥启用抓包识别后开始演奏，可读取乐谱与时长。";
        if (!visible) return;
        var room = ViewConnected ? ViewRoom : null;
        if (room is { ValueKind: JsonValueKind.Object } r)
        {
            var active = performance is { ValueKind: JsonValueKind.Object } p && p.GetProperty("phase").GetString() is not ("cancelled" or "stopped");
            var leader = active ? performance!.Value.GetProperty("leaderId").GetString() : r.GetProperty("leaderId").GetString();
            foreach (var member in r.GetProperty("members").EnumerateArray())
            {
                if (member.GetProperty("id").GetString() != leader) continue;
                if (member.TryGetProperty("music", out var score) && score.ValueKind == JsonValueKind.Object)
                {
                    var title = score.GetProperty("title").GetString();
                    var whole = score.GetProperty("wholeScoreDurationSeconds"); var selected = score.GetProperty("selectedDurationSeconds");
                    ScoreObservationText.Foreground=ColorBrush(whole.ValueKind==JsonValueKind.Number?"#86E1B2":"#F6D58B");
                    ScoreObservationText.Text = $"总指挥乐谱：{(string.IsNullOrWhiteSpace(title) ? "未命名乐谱" : title)} · " +
                        (whole.ValueKind == JsonValueKind.Number ? $"完整乐谱 {whole.GetDouble():0.###} 秒" : "时长待解析") +
                        (selected.ValueKind == JsonValueKind.Number ? $" · 当前音轨 {selected.GetDouble():0.###} 秒" : "") +
                        (active && !ScoreBelongsToRound(score,performance!.Value)?" · 等待本轮起点":"");
                    ScoreObservationText.ToolTip=score.GetProperty("issue").GetString() ?? "乐谱时长已识别，游戏中的开始与循环时间仍需核对。";
                    return;
                }
                ScoreObservationText.Text = "乐谱：等待识别";
                return;
            }
        }
        var local = _gameObservation.Current.Game.Music;
        ScoreObservationText.Text = local is null ? "乐谱：等待识别" :
            $"本机乐谱：{local.Title} · " + (local.WholeScoreDurationSeconds is { } seconds ? $"完整乐谱 {seconds:0.###} 秒" : "时长待解析");
        if(local is not null){ScoreObservationText.ToolTip=local.Issue;ScoreObservationText.Foreground=ColorBrush(local.WholeScoreDurationSeconds is not null?"#86E1B2":"#F6D58B");}
    }
    private static bool ScoreBelongsToRound(JsonElement score,JsonElement performance)
    {
        if(performance.GetProperty("phase").GetString() is not ("committed" or "performing" or "interrupted"))return false;
        if(!score.TryGetProperty("estimatedStartServerMs",out var onset) || onset.ValueKind!=JsonValueKind.Number)return false;
        var earliest=performance.TryGetProperty("earliestStartServerMs",out var first) && first.ValueKind==JsonValueKind.Number
            ? first.GetDouble():performance.GetProperty("startAction").GetProperty("targetServerMs").GetDouble();
        if(!double.IsFinite(onset.GetDouble()) || onset.GetDouble()<earliest-250)return false;
        if(!performance.TryGetProperty("stopRule",out var rule) || rule.GetString()!="first-cycle")return true;
        // For first-cycle mode, use the same score binding accepted by the room
        // rather than treating music still observed from a previous round as new.
        return performance.TryGetProperty("cycleScore",out var bound) && bound.ValueKind==JsonValueKind.Object &&
            score.TryGetProperty("concertId",out var concert) && bound.GetProperty("concertId").ToString()==concert.ToString() &&
            score.TryGetProperty("scoreSha256",out var hash) && bound.GetProperty("scoreSha256").GetString()==hash.GetString();
    }
    private string InterruptionText(JsonElement detail)
    {
        var name = detail.TryGetProperty("memberId", out var member) ? _members.FirstOrDefault(m => m.Id == member.GetString())?.Nickname ?? "成员" : "本轮";
        if (detail.TryGetProperty("evidenceSource", out var source) && source.GetString() == "player-report") return $"{name} 手动报告已提前停止";
        var reason = detail.TryGetProperty("source", out var s) ? s.GetString() switch
        {
            "tool-connection" or "tool-session-replaced" => "工具连接中断，请确认游戏状态。",
            "participant-not-ready" => "按键或按钮尚未设置好", "input-failure" => "操作失败或未能按时执行", "not-ready" => "取消就绪", "local-stop" => "执行本机停止",
            "prepare-timeout" or "prepare-too-late" => "未能及时准备好", "host-cancel" => "房主取消",
            "member-left" => "离开房间", "game-observation" => "报告演奏中断", _ => "本轮已取消或中断"
        } : "本轮已取消或中断";
        return name + "：" + reason + (detail.TryGetProperty("detail", out var error) && error.ValueKind == JsonValueKind.String ? " · " + Friendly(error.GetString() ?? "") : "");
    }
    private void ConsumeEndedMusicReadiness(JsonElement room, string memberId)
    {
        var p = room.GetProperty("performance");
        if (p.ValueKind != JsonValueKind.Object || p.GetProperty("mode").GetString() == "emote" || p.GetProperty("phase").GetString() is not ("cancelled" or "stopped") ||
            !p.GetProperty("participantIds").EnumerateArray().Any(id => id.GetString() == memberId)) return;
        var id = p.GetProperty("id").GetString();
        if (_consumedMusicRound == id) return;
        _consumedMusicRound = id; _inputRouter.StartsEnabled = false; _gameInput.RevokeChatDraft();
        _scheduler.CancelPendingStarts();
    }
}
