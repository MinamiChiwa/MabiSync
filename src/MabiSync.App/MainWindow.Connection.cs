using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Globalization;
using MabiSync.Core;

namespace MabiSync.App;

public partial class MainWindow
{
    private int _countdownMs = 5000;
    private string? _countdownError;
    private bool _connectionBusy;
    private bool _wasInRoom;
    private bool _syncTimingText;
    private int TimingOffsetMs => (int)Math.Round(TimingOffsetSlider.Value);
    private void TimingOffsetChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady || _closing) return;
        _scheduler.TimingOffsetMs = TimingOffsetMs;
        InvalidateReadiness();
        RefreshTimingLabel(); SaveSettings();
    }
    private void TimingOffsetTextChanged(object sender,TextChangedEventArgs e)
    {
        if(!_uiReady||_closing||_syncTimingText)return;
        try {
            var value=TimingAdjustmentText.ParseMilliseconds(TimingOffsetBox.Text);
            TimingOffsetBox.ClearValue(ToolTipProperty);
            TimingOffsetSlider.Value=value;
        } catch(FormatException error){TimingOffsetBox.ToolTip=error.Message;}
    }
    private void TimingOffsetBlurred(object sender,KeyboardFocusChangedEventArgs e)=>RefreshTimingLabel();
    private void TimingOffsetKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key is Key.Enter or Key.Escape){e.Handled=true;RefreshTimingLabel();Keyboard.ClearFocus();}
    }
    private void RefreshTimingLabel()
    {
        TimingOffsetValue.Text=TimingOffsetMs switch {<0=>$"{TimingOffsetMs} ms · 提前",>0=>$"+{TimingOffsetMs} ms · 延后",_=>"0 ms · 不调整"};
        TimingAdjustmentSection.Header="本机时间微调 · "+TimingOffsetValue.Text;
        StatusTimingSummary.Text="本机微调："+TimingOffsetValue.Text;
        _syncTimingText=true;
        try{TimingOffsetBox.Text=TimingOffsetMs.ToString(CultureInfo.InvariantCulture);}
        finally{_syncTimingText=false;}
    }
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
        CopyPublicButton.IsEnabled = !_copyingInvitation && ViewConnected && ViewSession is not null && _tunnel.State.Status == "ready";
        RefreshCopyStatus();
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
    private string RoomNickname()
    {
        var nickname=NicknameBox.Text.Trim();
        if(nickname.Length>0)return nickname;
        NicknameBox.Focus();
        throw new InvalidOperationException("请填写游戏昵称，或通过抓包识别更新昵称。");
    }
    private async void QuickCreateClick(object sender, RoutedEventArgs e) => await RunConnection((Button)sender, async () =>
    {
        RequireNoRoom();
        var nickname=RoomNickname();
        var executable = Path.Combine(AppContext.BaseDirectory, "runtime", "cloudflared.exe");
        if (!File.Exists(executable)) throw new IOException("当前构建缺少公网组件，请使用完整发布包，或展开高级连接使用局域网 / 手动服务。");
        MessageText.Text = "正在创建房间并验证公网连接，请稍候……";
        await _server.StartAsync(LanCheck.IsChecked == true);
        EndpointBox.Text = "ws://127.0.0.1:3132";
        await _client.ConnectAsync(EndpointBox.Text);
        await _client.CreateRoomAsync(nickname);
        SaveSettings();
        await _tunnel.StartAsync(executable);
        if (_tunnel.State.Status == "ready") Feedback("公网房间已就绪，点击“复制邀请”分享给好友。");
        else MessageText.Text = "本机房间已创建，公网尚未就绪。请查看连接提示，在高级连接中重试建立公网连接。";
    });
    private async void JoinInvitationClick(object sender, RoutedEventArgs e) => await RunConnection((Button)sender, async () =>
    {
        RequireNoRoom();
        var nickname=RoomNickname();
        var invitation = RoomInvitation.Parse(InvitationBox.Text);
        MessageText.Text = "正在连接邀请中的房间……";
        await _tunnel.StopAsync();
        EndpointBox.Text = invitation.Endpoint.AbsoluteUri; CodeBox.Text = invitation.Code;
        await _client.ConnectAsync(invitation.Endpoint.AbsoluteUri);
        await _client.JoinRoomAsync(nickname, invitation.Code);
        SaveSettings(); Feedback("已加入房间，请准备本机动作并点击底部“本机就绪”。");
    });
}
