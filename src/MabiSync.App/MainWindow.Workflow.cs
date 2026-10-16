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
            SelectedMode == "ensemble" && self is not null ? $"{(host ? "房主" : "成员")} · {self.GroupText} · {self.EnsembleRoleText} · 第 {self.Order} 位" : host ? "房主 · 管理本轮参与者与开始时间" : "成员 · 准备本机动作";
        StartButton.Visibility = Show(host);
        ReadyButton.Visibility = Show(inRoom && !host);
        CancelButton.Visibility = Show(host && active && beforeStart);
        StopButton.Visibility = Show(host && music && active);
        LocalStopButton.Visibility = Show(active && selected && LocalStopButton.IsEnabled);
        CountdownControls.Visibility = Show(host);
        StopControls.Visibility = Show(music && (!inRoom || host));
        LeaderControls.Visibility = Show(music && host);
        CopyPublicButton.Visibility = Show(host && _tunnel.State.Status == "ready");
        LeaveRoomButton.Visibility = Show(inRoom);
        LeaveRoomButton.Content=host?"关闭房间":"离开房间";
        PublicStateText.Visibility = Show(!inRoom || host && _tunnel.State.Status != "off");
        RosterCard.Visibility = Show(inRoom);
        LocalActionCard.Visibility = System.Windows.Visibility.Visible;
        LocalActionTitle.Text = IsManualEnsembleJoiner ? "3  本机停止设置" : "3  本机动作";
        ReadyButton.Style = (Style)FindResource(host ? "SecondaryAction" : "PrimaryAction");
    }

    private void RefreshNextStep()
    {
        if (ViewSession is null) { NextStepText.Text = "创建房间，或粘贴邀请加入。"; return; }
        if (!ViewConnected) { NextStepText.Text = "连接已断开，请在高级连接中重连。"; return; }
        var self = _members.FirstOrDefault(m => m.Id == ViewSession.MemberId);
        var p = ViewRoom?.GetProperty("performance");
        if (p is { ValueKind: JsonValueKind.Object } round && round.GetProperty("phase").GetString() is not ("cancelled" or "stopped"))
        {
            if (!round.GetProperty("participantIds").EnumerateArray().Any(id => id.GetString() == self?.Id))
            { NextStepText.Text = IsRoomHost ? "本轮由选中的成员执行，你可以取消或统一停止。" : "你正在旁观本轮。"; return; }
            NextStepText.Text = round.GetProperty("phase").GetString() == "interrupted"
                ? IsRoomHost ? "请选择全员停止，或继续演奏。" : "等待房主决定停止或继续。"
                : IsManualEnsembleJoiner ? self?.JoinState == "correct" ? "加入顺序正确，可继续在游戏中演奏。"
                    : self?.JoinState == "wrong" ? "加入顺序与安排不符，请与本组发起者确认。" : "靠近本组发起者，按成员列表中的顺序加入。"
                : _inputRouter.UseSimulation ? "本轮运行模拟，可在执行记录中查看结果。" : "开始前自动激活所选游戏窗口，请保持游戏在前台。";
            return;
        }
        if (!_inputRouter.UseSimulation)
        {
            if (SelectedWindow is null) { NextStepText.Text = "请在本机动作中选择游戏窗口。"; return; }
            if (_bindingError is not null) { NextStepText.Text = _bindingError; return; }
            var binding = _modeBindings[SelectedMode];
            if (SelectedMode != "emote" && binding.Stop is null) { NextStepText.Text = "请记录画面上的停止按钮，或设置停止快捷键。"; return; }
            if (!IsManualEnsembleJoiner && binding.Start is null) { NextStepText.Text = "请设置本机动作的快捷键或按钮。"; return; }
        }
        var prepared = _inputRouter.UseSimulation ? "本机设置已就绪" : IsManualEnsembleJoiner ? "停止设置已准备好" :
            SelectedMode == "emote" && _modeBindings[SelectedMode].Start is MabiSync.Core.ChatDraftBinding ? "先在游戏聊天框预填动作指令" : SelectedMode == "emote" ? "准备好要触发的动作" : "先在背包中选好乐谱";
        if (IsRoomHost)
        {
            var waiting = _members.Where(m => m.Selected && !m.Ready && m.Id != self?.Id).Select(m => m.Nickname).ToArray();
            NextStepText.Text = waiting.Length > 0
                ? "等待就绪：" + string.Join("、", waiting) + "。可在成员列表调整本轮参与者。" : prepared + "；确认参与者和倒计时后点击开始。";
        }
        else NextStepText.Text = self?.Ready == true ? "已就绪，等待房主开始。" : prepared + "，然后点击本机就绪。";
    }
}
