using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MabiSync.Core;

namespace MabiSync.App;

public partial class MainWindow
{
    private string? _musicDurationError, _consumedMusicRound;
    private long _musicDurationMs = 30000;
    private void MusicDurationChanged(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady || _closing) return;
        try { _musicDurationMs = MusicDurationText.ParseMilliseconds(DurationBox.Text); _musicDurationError = null; SaveSettings(); }
        catch (FormatException error) { _musicDurationError = error.Message; }
        Refresh();
    }
    private async void ReportLeaderClick(object sender, RoutedEventArgs e) => await Run((Button)sender, async () =>
    {
        if (ViewRoom?.GetProperty("performance") is not { ValueKind: JsonValueKind.Object } p) throw new InvalidOperationException("当前没有演奏场次。");
        await _client.SendAsync(new { type = "leader-status", sessionId = p.GetProperty("id").GetString(), status = "interrupted", evidenceSource = "player-report" });
        MessageText.Text = "已通知房主选择全员停止或继续。";
    });
    private void RefreshMusicStatus(JsonElement? performance)
    {
        LeaderPurposeText.Text=SelectedStopRule switch {
            MusicStopRule.FirstCycle=>"以此人的乐谱确定全员一轮停止时间；总指挥中断时提示房主。各组由各自的发起者开始。",
            MusicStopRule.Duration=>"显示此人的乐谱，作为中断报告对象；全员按指定时长停止。",
            _=>"显示此人的乐谱，作为中断报告对象；全员停止由房主决定。"
        };
        var music = SelectedMode != "emote";
        MusicStatusText.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        RefreshScoreObservation(music, performance);
        ReportLeaderButton.Visibility = ContinueButton.Visibility = Visibility.Collapsed;
        if (!music) return;
        var host = ViewSession is { } self && ViewRoom?.GetProperty("hostId").GetString() == self.MemberId;
        var active = performance is { ValueKind: JsonValueKind.Object } p && p.GetProperty("phase").GetString() is not ("cancelled" or "stopped");
        if (performance is not { ValueKind: JsonValueKind.Object } value || value.GetProperty("mode").GetString() == "emote")
        { MusicStatusText.Text = host ? SelectedStopRule switch {
            MusicStopRule.Duration => _musicDurationError ?? $"结束方式：演奏 {_musicDurationMs / 1000m:0.###} 秒后全员停止。",
            MusicStopRule.FirstCycle => "结束方式：总指挥一轮后全员停止。总指挥需启用抓包识别；停止点按起点估计与全谱时长安排。",
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
        ContinueButton.Content = hasStop && committed ? "继续到原定停止点" : "继续演奏，稍后手动停止";
        var ending = hasStop
            ? !committed ? "正在等待全员确认停止动作" : $"本机计划停止还剩 {Math.Max(0, (_scheduler.LocalDeadline(stop.GetProperty("id").GetString()!) is { } deadline ? deadline - _clock.NowMs : stop.GetProperty("targetServerMs").GetDouble() + TimingOffsetMs - (now ?? 0)) / 1000):0.0} 秒（{(automatic?"总指挥一轮 · 抓包估计":"手动时长")}）"
            : automatic ? CycleWaitingText(value) : "由房主手动全员停止";
        var detail = value.GetProperty("interruption");
        var interruption = detail.ValueKind == JsonValueKind.Object ? InterruptionText(detail) : "";
        var failures = value.GetProperty("receipts").EnumerateArray().Count(r => r.GetProperty("status").GetString() is "failed" or "missed");
        var warning = failures > 0 ? $"有 {failures} 条输入失败或错过，请展开回执并核对游戏。" : "";
        MusicStatusText.Text = phase switch
        {
            "interrupted" => $"总指挥：{leader} · {interruption}。{ending}。由房主决定全停或继续。",
            "cancelled" => $"本轮已取消：{interruption}。下一轮请重新准备并就绪。",
            "stopped" => warning + "本轮停止计划已结束，请核对游戏是否已停止；下一轮需重新准备并就绪。",
            _ => $"总指挥：{leader} · {ending}。"
        };
    }
    private static string CycleWaitingText(JsonElement performance) => performance.TryGetProperty("cycleStopStatus",out var status) ? status.GetString() switch
    {
        "too-late" => "收到谱面时已接近首轮终点，请由房主手动停止",
        "participant-not-ready" => "有成员的停止动作未准备好，请由房主手动停止",
        "prepare-timeout" => "停止动作未及时完成确认，请由房主手动停止",
        "interrupted-before-stop-commit" => "停止计划尚未提交，请由房主决定继续或全停",
        _ => "等待总指挥的新乐谱与起点；当前可由房主手动停止"
    } : "等待总指挥的乐谱与起点";
    private void RefreshScoreObservation(bool visible, JsonElement? performance)
    {
        ScoreCard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
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
                    ScoreObservationText.Text = $"总指挥乐谱：{(string.IsNullOrWhiteSpace(title) ? "未命名乐谱" : title)} · " +
                        (whole.ValueKind == JsonValueKind.Number ? $"全谱 {whole.GetDouble():0.###} 秒" : "时长待解析") +
                        (selected.ValueKind == JsonValueKind.Number ? $" · 实际声部 {selected.GetDouble():0.###} 秒" : "") +
                        "\n" + (score.GetProperty("issue").GetString() ?? "游戏起点与循环终点待核对。");
                    return;
                }
                ScoreObservationText.Text = "等待总指挥乐谱信息；总指挥启用抓包识别后开始演奏即可读取。";
                return;
            }
        }
        var local = _gameObservation.Current.Game.Music;
        ScoreObservationText.Text = local is null ? "启用抓包识别后，可查看正在演奏的乐谱信息。" :
            $"本机乐谱：{local.Title} · " + (local.WholeScoreDurationSeconds is { } seconds ? $"全谱 {seconds:0.###} 秒" : "时长待解析") + "\n" + local.Issue;
    }
    private string InterruptionText(JsonElement detail)
    {
        var name = detail.TryGetProperty("memberId", out var member) ? _members.FirstOrDefault(m => m.Id == member.GetString())?.Nickname ?? "成员" : "本轮";
        if (detail.TryGetProperty("evidenceSource", out var source) && source.GetString() == "player-report") return $"{name} 手动报告已提前停止";
        var reason = detail.TryGetProperty("source", out var s) ? s.GetString() switch
        {
            "tool-connection" or "tool-session-replaced" => "工具连接中断，请确认游戏状态。",
            "participant-not-ready" => "本机动作准备失败", "input-failure" => "输入失败或错过", "not-ready" => "取消就绪", "local-stop" => "执行本机停止",
            "prepare-timeout" or "prepare-too-late" => "未及时完成动作准备", "host-cancel" => "房主取消",
            "member-left" => "离开房间", "game-observation" => "报告演奏中断", _ => "任务取消或发生中断"
        } : "任务取消或发生中断";
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
