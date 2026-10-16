using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MabiSync.App;

public partial class MainWindow
{
    private const string RosterDragFormat = "MabiSync.EnsembleMember";
    private Point _rosterPointer;
    private string? _dragMemberId;
    private CaptureChannelWindow? _captureChannels;
    private void CaptureChannelsClick(object sender, RoutedEventArgs e)
    {
        if (_captureChannels is not null) { _captureChannels.Activate(); return; }
        _captureChannels = new(SelectedWindow, _gameObservation) { Owner = this };
        ConfigureAuxiliaryWindow(_captureChannels);
        _captureChannels.GameSelected += SelectCaptureGameWindow;
        _captureChannels.Closed += (_, _) => _captureChannels = null;
        _captureChannels.Show();
    }
    private async void RefreshObservationClick(object sender, RoutedEventArgs e) => await Run((Button)sender, async () =>
    {
        if (!_gameObservation.Current.Running)
        {
            CaptureChannelsClick(sender, e);
            MessageText.Text = "选择游戏和网卡后点击开始，即可更新昵称。";
            return;
        }
        await _observationSend;
        _reportedObservation = null;
        ObservationTick();
        await _observationSend;
        var state = _gameObservation.Current;
        MessageText.Text = state.Game.Identity is null ? state.Status : $"已识别昵称：{state.Game.Identity.Nickname}。加入顺序随抓包自动更新。";
    });
    private bool IsRoomHost => ViewSession is { } self && ViewRoom?.GetProperty("hostId").GetString() == self.MemberId;
    private bool IsManualEnsembleJoiner => SelectedMode == "ensemble" && _members.FirstOrDefault(m => m.Id == ViewSession?.MemberId)?.EnsembleRole == "joiner";
    private bool RoundActive => ViewRoom?.GetProperty("performance") is { ValueKind: JsonValueKind.Object } p && p.GetProperty("phase").GetString() is not ("cancelled" or "stopped");
    private bool CanEditEnsemble => SelectedMode == "ensemble" && IsRoomHost && ViewConnected && !RoundActive;
    private object LocalCapabilities() => new { simulation = _inputRouter.UseSimulation, canStart = !IsManualEnsembleJoiner,
        canStop = SelectedMode != "emote", timingOffsetMs = TimingOffsetMs, readyMode = SelectedMode,
        clockRoundTripMs=_roomClock.Estimate?.RoundTripMs??0,
        windowActivationMs=_inputRouter.UseSimulation?0:1500,
        musicRole = IsManualEnsembleJoiner ? "joiner" : "initiator" };
    private void RefreshEnsembleRoster()
    {
        SyncEnsembleInputPermission();
        var ensemble = SelectedMode == "ensemble";
        RosterSummaryText.Text=$"{_members.Count} 人 · 本轮 {_members.Count(m=>m.Selected)} 人";
        var visibility = ensemble ? Visibility.Visible : Visibility.Collapsed;
        GroupColumn.Visibility = OrderColumn.Visibility = EnsembleRoleColumn.Visibility = InstrumentColumn.Visibility = visibility;
        EnsembleRoleText.Visibility = EnsembleRosterHint.Visibility = visibility;
        JoinFeedbackColumn.Visibility = visibility;
        RoomRoleColumn.Visibility=ensemble?Visibility.Collapsed:Visibility.Visible;
        LayoutRosterColumns();
        RosterTitle.Text = ensemble ? "4  合奏成员" : "4  本轮成员";
        EnsembleRosterHint.Text = IsRoomHost ? "房主可编辑分组、顺序、身份与乐器。" : "找到自己的组别、顺序与乐器；乐谱使用者开始后，依序在游戏中加入。";
        MembersGrid.Columns.First(c => c is DataGridCheckBoxColumn).IsReadOnly = !IsRoomHost || RoundActive;
        if (ViewRoom is not { } room) { EnsembleRoleText.Text = "入房后由房主分组并指定乐谱使用者。"; return; }
        var self = _members.FirstOrDefault(m => m.Id == ViewSession?.MemberId);
        EnsembleRoleText.Text = self is null ? "等待房间成员信息。" : IsManualEnsembleJoiner
            ? $"{self.GroupText} · 第 {self.Order} 位加入。设置停止方式后就绪，乐谱使用者开始后在游戏中加入。"
            : $"{self.GroupText} · 乐谱使用者。在背包中选好乐谱，设置乐谱使用按钮与停止快捷键。";
        var rows = ensemble ? _members.OrderBy(m => m.Group).ThenBy(m => m.Order).ToArray()
            : room.GetProperty("members").EnumerateArray().Select(m => _members.FirstOrDefault(row => row.Id == m.GetProperty("id").GetString())).OfType<MemberRow>().ToArray();
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var at = _members.IndexOf(row); if (at != i) _members.Move(at, i);
        }
    }
    private static T? Ancestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null) { if (source is T value) return value; source = VisualTreeHelper.GetParent(source); }
        return null;
    }
    private void RosterPointerDown(object sender, MouseButtonEventArgs e)
    {
        _dragMemberId = null;
        if (!CanEditEnsemble || Ancestor<CheckBox>(e.OriginalSource as DependencyObject) is not null || Ancestor<TextBox>(e.OriginalSource as DependencyObject) is not null || Ancestor<ComboBox>(e.OriginalSource as DependencyObject) is not null) return;
        _rosterPointer = e.GetPosition(MembersGrid);
        _dragMemberId = (Ancestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as MemberRow)?.Id;
    }
    private void RosterPointerMove(object sender, MouseEventArgs e)
    {
        if (!CanEditEnsemble || e.LeftButton != MouseButtonState.Pressed || _dragMemberId is not { } id) return;
        var at = e.GetPosition(MembersGrid);
        if (Math.Abs(at.X - _rosterPointer.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(at.Y - _rosterPointer.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragMemberId = null;
        DragDrop.DoDragDrop(MembersGrid, new DataObject(RosterDragFormat, id), DragDropEffects.Move);
    }
    private void RosterDragOver(object sender, DragEventArgs e)
    {
        e.Effects = CanEditEnsemble && e.Data.GetDataPresent(RosterDragFormat) && Ancestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is MemberRow ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }
    private async void RosterDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!CanEditEnsemble || e.Data.GetData(RosterDragFormat) is not string id || !_members.Any(m => m.Id == id) ||
            Ancestor<DataGridRow>(e.OriginalSource as DependencyObject) is not { Item: MemberRow target } row || target.Id == id) return;
        var placement = e.GetPosition(row).Y < row.ActualHeight / 2 ? "before" : "after";
        await Run(null, () => _client.SendAsync(new { type = "move-ensemble-member", memberId = id, targetMemberId = target.Id, placement }));
    }
    private void RosterRightClick(object sender, MouseButtonEventArgs e)
    {
        MembersGrid.SelectedItem = Ancestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as MemberRow;
    }
    private void RosterMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = MembersGrid.ContextMenu; menu.Items.Clear();
        if (SelectedMode != "ensemble" || !IsRoomHost || !ViewConnected || MembersGrid.SelectedItem is not MemberRow member) { e.Handled = true; return; }
        void Add(ItemsControl parent, string label, Func<Task> action, bool enabled = true)
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled };
            item.Click += async (_, _) => await Run(null, action); parent.Items.Add(item);
        }
        Add(menu, member.Selected ? "不参与本轮" : "参与本轮", () => { member.Selected = !member.Selected; return Task.CompletedTask; }, !RoundActive);
        Add(menu, "设为总指挥（乐谱与一轮停止参考）", () => _client.SendAsync(new { type = "select-leader", memberId = member.Id }), !RoundActive);
        Add(menu, member.EnsembleRole == "initiator" ? "改为手动加入的参与者" : "设为本组乐谱使用者（替换原乐谱使用者）", () => _client.SendAsync(new { type = "set-ensemble-role", memberId = member.Id, musicRole = member.EnsembleRole == "initiator" ? "joiner" : "initiator" }), !RoundActive);
        var identities = new MenuItem { Header = "关联已观测角色（未安装 Npcap 的成员）" };
        if (ViewRoom is { } observedRoom && observedRoom.TryGetProperty("observedCharacters", out var characters))
            foreach (var character in characters.EnumerateArray())
            {
                var characterId = character.GetProperty("characterId").GetString()!;
                var realmId = character.GetProperty("realmId").GetInt32();
                Add(identities, $"{character.GetProperty("nickname").GetString()} · 区服 {realmId}",
                    () => _client.SendAsync(new { type = "bind-game-character", memberId = member.Id, characterId, realmId }));
            }
        if (identities.Items.Count == 0) identities.Items.Add(new MenuItem { Header = "等待观测端取得完整合奏成员", IsEnabled = false });
        Add(identities, "清除手动关联", () => _client.SendAsync(new { type = "bind-game-character", memberId = member.Id, characterId = (string?)null }));
        menu.Items.Add(identities);
        var same = _members.Where(m => m.Group == member.Group).OrderBy(m => m.Order).ToArray();
        var index = Array.IndexOf(same, member);
        if (index > 0) Add(menu, "组内上移", () => _client.SendAsync(new { type = "move-ensemble-member", memberId = member.Id, targetMemberId = same[index - 1].Id, placement = "before" }), !RoundActive);
        if (index < same.Length - 1) Add(menu, "组内下移", () => _client.SendAsync(new { type = "move-ensemble-member", memberId = member.Id, targetMemberId = same[index + 1].Id, placement = "after" }), !RoundActive);
        var groups = new MenuItem { Header = "移到分组", IsEnabled = !RoundActive }; menu.Items.Add(groups);
        var existing = _members.Select(m => m.Group).Distinct().Order().ToArray();
        foreach (var group in existing) Add(groups, $"第 {group} 组（排在末尾）", () => _client.SendAsync(new { type = "move-ensemble-member", memberId = member.Id, group }), group != member.Group);
        var next = Enumerable.Range(1, 24).FirstOrDefault(g => !existing.Contains(g));
        if (next != 0) Add(groups, $"新建第 {next} 组", () => _client.SendAsync(new { type = "move-ensemble-member", memberId = member.Id, group = next }));
        if (member.Id != ViewSession?.MemberId)
        {
            menu.Items.Add(new Separator());
            Add(menu, "移出房间", () => _client.SendAsync(new { type = "kick-member", memberId = member.Id }));
        }
    }
}
