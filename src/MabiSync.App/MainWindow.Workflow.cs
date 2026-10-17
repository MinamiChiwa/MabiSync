using System.Text.Json;
using System.Windows;

namespace MabiSync.App;

public partial class MainWindow
{
    private void RefreshRoleLayout(bool active, bool beforeStart)
    {
        var inRoom = ViewSession is not null;
        var host = inRoom && IsRoomHost;
        var music = SelectedMode != "emote";
        var self = _members.FirstOrDefault(m => m.Id == ViewSession?.MemberId);
        var selected = !active || ViewRoom?.GetProperty("performance").GetProperty("participantIds")
            .EnumerateArray().Any(id => id.GetString() == self?.Id) == true;
        static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

        RoleSummaryText.Text = !inRoom ? "创建房间，或接受好友邀请" :
            SelectedMode == "ensemble" && self is not null ? $"{(host ? "房主" : "成员")} · {self.GroupText} · {self.EnsembleRoleText} · 第 {self.Order} 位 · 乐器：{self.Instrument}" : host ? "房主 · 管理本轮参与者与开始时间" : "成员 · 准备本机动作";
        StartButton.Visibility = Show(host);
        ReadyButton.Visibility = Show(inRoom && !host);
        CancelButton.Visibility = Show(host && active && beforeStart);
        StopButton.Visibility = Show(host && music && active);
        LocalStopButton.Visibility = Show(active && selected && LocalStopButton.IsEnabled);
        CountdownControls.Visibility = Show(host);
        StopControls.Visibility = Show(music && (!inRoom || host));
        LeaderControls.Visibility = Show(music && host);
        CopyPublicButton.Visibility = Show(host && ShareReady);
        LeaveRoomButton.Visibility = Show(inRoom);
        LeaveRoomButton.Content=host?"关闭房间":"离开房间";
        PublicStateText.Visibility = Show(!inRoom || ActiveNetworkState.Status != "off");
        RosterCard.Visibility = Show(inRoom);
        LocalActionCard.Visibility = System.Windows.Visibility.Visible;
        LocalActionTitle.Text = "3  本机动作";
        ReadyButton.Style = (Style)FindResource(self?.Ready == true ? "ReadyAction" : "StopAction");
    }

    private void RefreshNextStep()
    {
        if (ViewSession is null) { NextStepText.Text = "创建房间，或粘贴邀请加入。"; return; }
        if (!ViewConnected) { NextStepText.Text = IsRoomHost?"连接已断开，可点击重新连接，或关闭房间。":"连接已断开，请点击重新连接；恢复后重新就绪。"; return; }
        var self = _members.FirstOrDefault(m => m.Id == ViewSession.MemberId);
        var p = ViewRoom?.GetProperty("performance");
        if (p is { ValueKind: JsonValueKind.Object } round && round.GetProperty("phase").GetString() is not ("cancelled" or "stopped"))
        {
            if (!round.GetProperty("participantIds").EnumerateArray().Any(id => id.GetString() == self?.Id))
            { NextStepText.Text = IsRoomHost ? "选中的成员正在参与本轮。你可以取消本轮或全员停止。" : "你正在旁观本轮。"; return; }
            NextStepText.Text = round.GetProperty("phase").GetString() == "interrupted"
                ? IsRoomHost ? "请选择全员停止，或继续演奏。" : "等待房主决定停止或继续。"
                : IsManualEnsembleJoiner ? self?.JoinState == "correct" ? "加入顺序正确，可继续在游戏中演奏。"
                    : self?.JoinState == "wrong" ? "加入顺序与安排不符，请与本组乐谱使用者确认。" : "靠近本组乐谱使用者，按成员列表中的顺序加入。"
                : _inputRouter.UseSimulation ? "正在模拟本轮操作，可在“执行记录与诊断”中查看结果。" : "开始前自动激活所选游戏窗口，请保持游戏在前台。";
            return;
        }
        if (!_inputRouter.UseSimulation)
        {
            if (SelectedWindow is null) { NextStepText.Text = "请在本机动作中选择游戏窗口。"; return; }
            if (_bindingError is not null) { NextStepText.Text = _bindingError; return; }
            var binding = _modeBindings[SelectedMode];
            if (SelectedMode != "emote" && binding.Stop is null) { NextStepText.Text = "请记录画面上的停止按钮，或设置停止快捷键。"; return; }
            if (!IsManualEnsembleJoiner && binding.Start is null) { NextStepText.Text = "请先设置开始按键，或点选游戏中的开始按钮。"; return; }
        }

        if (IsRoomHost)
        {
            var waiting = _members.Where(m => m.Selected && !m.Ready && m.Id != self?.Id).Select(m => m.Nickname).ToArray();
            NextStepText.Text = waiting.Length > 0
                ? "未就绪：" + string.Join("、", waiting.Take(3)) + (waiting.Length>3?$" 等 {waiting.Length} 人":"") : "点击开始";
        }
        else NextStepText.Text = self?.Ready == true ? "等待房主开始" : "确认本机设置后点击就绪";
    }
}
