using System.IO;
using System.Windows;
using System.Windows.Controls;
using MabiSync.Core;

namespace MabiSync.App;

public partial class MainWindow
{
    private int _countdownMs = 5000;
    private string? _countdownError;
    private bool _connectionBusy;
    private bool _wasInRoom;
    private int TimingOffsetMs => (int)Math.Round(TimingOffsetSlider.Value);
    private void TimingOffsetChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady || _closing) return;
        if (_inputRouter.StartsEnabled) InvalidateReadiness();
        _scheduler.TimingOffsetMs = TimingOffsetMs;
        RefreshTimingLabel(); SaveSettings();
    }
    private void RefreshTimingLabel() => TimingOffsetValue.Text = TimingOffsetMs switch
    {
        < 0 => $"{TimingOffsetMs} ms · 提前",
        > 0 => $"+{TimingOffsetMs} ms · 延后",
        _ => "0 ms · 不调整"
    };
    private void ResetTimingClick(object sender, RoutedEventArgs e) => TimingOffsetSlider.Value = 0;
    private void CountdownChanged(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady || _closing) return;
        try { _countdownMs = CountdownText.ParseMilliseconds(CountdownBox.Text); _countdownError = null; SaveSettings(); }
        catch (FormatException error) { _countdownError = error.Message; }
        Refresh();
    }
    private void NicknameChanged(object sender, TextChangedEventArgs e) { if (_uiReady && !_closing) SaveSettings(); }
    private void RefreshConnection()
    {
        RoomControls.IsEnabled = !_connectionBusy;
        var inRoom = ViewSession is not null;
        QuickCreateButton.Visibility = InvitationControls.Visibility = inRoom ? Visibility.Collapsed : Visibility.Visible;
        if (inRoom && !_wasInRoom) AdvancedConnection.IsExpanded = false;
        _wasInRoom = inRoom;
        QuickCreateButton.IsEnabled = JoinInvitationButton.IsEnabled = !inRoom;
        CopyPublicButton.IsEnabled = ViewConnected && ViewSession is not null && _tunnel.State.Status == "ready";
        NicknameBox.IsEnabled = ViewSession is null;
    }
    private async Task RunConnection(Button button, Func<Task> operation)
    {
        if (_connectionBusy || _closing) return;
        _connectionBusy = true; RefreshConnection();
        try { await Run(button, operation); }
        finally { _connectionBusy = false; if (!_closing) Refresh(); }
    }
    private void RequireNoRoom() { if (ViewSession is not null) throw new InvalidOperationException("请先离开当前房间。"); }
    private async void QuickCreateClick(object sender, RoutedEventArgs e) => await RunConnection((Button)sender, async () =>
    {
        RequireNoRoom();
        var executable = Path.Combine(AppContext.BaseDirectory, "runtime", "cloudflared.exe");
        if (!File.Exists(executable)) throw new IOException("当前构建缺少公网组件，请使用完整发布包，或展开高级连接使用局域网 / 手动服务。");
        MessageText.Text = "正在创建房间并验证公网连接，请稍候……";
        await _server.StartAsync(LanCheck.IsChecked == true);
        EndpointBox.Text = "ws://127.0.0.1:3132";
        await _client.ConnectAsync(EndpointBox.Text);
        await _client.CreateRoomAsync(NicknameBox.Text.Trim());
        SaveSettings();
        await _tunnel.StartAsync(executable);
        if (_tunnel.State.Status == "ready") Feedback("公网房间已就绪，点击“复制邀请”分享给好友。");
        else MessageText.Text = "本机房间已创建，公网尚未就绪。请查看连接提示，在高级连接中重试建立公网连接。";
    });
    private async void JoinInvitationClick(object sender, RoutedEventArgs e) => await RunConnection((Button)sender, async () =>
    {
        RequireNoRoom();
        var invitation = RoomInvitation.Parse(InvitationBox.Text);
        MessageText.Text = "正在连接邀请中的房间……";
        await _tunnel.StopAsync();
        EndpointBox.Text = invitation.Endpoint.AbsoluteUri; CodeBox.Text = invitation.Code;
        await _client.ConnectAsync(invitation.Endpoint.AbsoluteUri);
        await _client.JoinRoomAsync(NicknameBox.Text.Trim(), invitation.Code);
        SaveSettings(); Feedback("已加入房间，请准备本机动作并点击底部“本机就绪”。");
    });
}
