using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Globalization;
using MabiSync.Core;

namespace MabiSync.App;

public partial class MainWindow
{
    private async Task CreateConfiguredRoom(string nickname, CancellationToken token=default)
    {
        var mode=SelectedMode;
        await _client.CreateRoomAsync(nickname,token);
        // Older services create emote rooms; apply the host's requested mode explicitly.
        await _client.SetRoomModeAsync(mode);
    }
    private int _countdownMs = 5000;
    private string? _countdownError;
    private bool _connectionBusy;
    private bool _leavingRoom;
    private Task? _connectionFinished;
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
        RoomControls.IsEnabled = true;
        var inRoom = ViewSession is not null;
        QuickCreateButton.Visibility = InvitationControls.Visibility = inRoom ? Visibility.Collapsed : Visibility.Visible;
        if (inRoom && !_wasInRoom) AdvancedConnection.IsExpanded = false;
        _wasInRoom = inRoom;
        QuickCreateButton.IsEnabled = JoinInvitationButton.IsEnabled = !inRoom && !_connectionBusy;
        OpenPublicButton.IsEnabled=ConnectButton.IsEnabled=!_connectionBusy;
        AdvancedConnectionControls.IsEnabled=!_connectionBusy;
        LeaveRoomButton.IsEnabled=!_leavingRoom && (!_connectionBusy || _networkOperation is not null);
        ReconnectRoomButton.Visibility=inRoom && (!ViewConnected || ActiveNetworkState.Status=="error")?Visibility.Visible:Visibility.Collapsed;
        ReconnectRoomButton.IsEnabled=!_connectionBusy;
        RefreshProvider();
        if(_usingCommunity || _usingLan)RenderNetwork(ActiveNetworkState);
        CopyPublicButton.IsEnabled = !_copyingInvitation && ViewConnected && ViewSession is not null && ShareReady;
        RefreshCopyStatus();
        NicknameBox.IsEnabled = ViewSession is null && !_connectionBusy;
    }
    private async Task RunConnection(Button button, Func<Task> operation)
    {
        if (_connectionBusy || _closing) return;
        var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);_connectionFinished=finished.Task;
        _connectionBusy = true; RefreshConnection();
        try { await Run(button, operation); }
        finally { _connectionBusy = false;_connectionFinished=null;finished.TrySetResult();if (!_closing) Refresh(); }
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
        if(SelectedProvider=="community"){await ConnectCommunity(true);return;}
        if(SelectedProvider=="lan"){await CreateLanRoom();return;}
        _usingCommunity=false;_usingLan=false;_lanEndpoint=null;
        var nickname=RoomNickname();
        MessageText.Text = "正在创建房间并验证公网连接，请稍候……";
        await _server.StartAsync(LanCheck.IsChecked == true);
        EndpointBox.Text = "ws://127.0.0.1:3132";
        await _client.ConnectAsync(EndpointBox.Text);
        await CreateConfiguredRoom(nickname);
        SaveSettings();
        await EstablishPublic();
        if (ActiveNetworkState.Status == "ready") Feedback("公网房间已就绪，点击“复制邀请”分享给好友。");
        else if(ActiveNetworkState.Status=="off")Feedback("公网连接已取消，本机房间仍保留。",FeedbackTone.Info);
        else MessageText.Text = "本机房间已创建，公网尚未就绪。请查看连接提示，在高级连接中重试建立公网连接。";
    });
    private async void JoinInvitationClick(object sender, RoutedEventArgs e) => await RunConnection((Button)sender, async () =>
    {
        RequireNoRoom();
        var nickname=RoomNickname();
        MessageText.Text = "正在连接邀请中的房间……";
        var text=InvitationBox.Text.Trim();
        if(SelectedProvider=="community" && !text.StartsWith("MabiSync/",StringComparison.Ordinal)) {
            await ConnectCommunity(false,text.ToUpperInvariant());return;
        }
        if(text.StartsWith("MabiSync/2",StringComparison.Ordinal))throw new FormatException("此版本已移除 EasyTier，请房主使用服务器直连、Cloudflare 或局域网重新分享邀请。");
        var invitation=SelectedProvider=="lan" && !text.StartsWith("MabiSync/",StringComparison.Ordinal)
            ? RoomInvitation.Parse(new RoomInvitation(ReadLanEndpoint(),text.ToUpperInvariant()).ToString()) : RoomInvitation.Parse(text);
        if(invitation.Endpoint==_communityEndpoint){PublicProviderBox.SelectedIndex=2;await ConnectCommunity(false,invitation.Code);return;}
        _usingCommunity=false;await StopNetworks();
        _usingLan=invitation.Endpoint.Scheme=="ws" && (SelectedProvider=="lan" || IsLocalNetwork(invitation.Endpoint));
        PublicProviderBox.SelectedIndex=_usingLan?1:0;
        _lanEndpoint=_usingLan?invitation.Endpoint:null;
        EndpointBox.Text=invitation.Endpoint.AbsoluteUri;CodeBox.Text=invitation.Code;
        await _client.ConnectAsync(invitation.Endpoint.AbsoluteUri);
        await _client.JoinRoomAsync(nickname,invitation.Code);
        InvitationBox.Clear();
        SaveSettings(); Feedback("已加入房间，请准备本机动作并点击底部“就绪”。");
    });
    private async void ReconnectRoomClick(object sender,RoutedEventArgs e)=>await RunConnection((Button)sender,ReconnectRoom);
    private async Task ReconnectRoom()
    {
        var host=IsRoomHost;
        using var operation=new CancellationTokenSource();_networkOperation=operation;CancelNetworkButton.Visibility=Visibility.Visible;
        Feedback("正在恢复房间连接…",FeedbackTone.Info);
        try {
            await _client.ConnectAsync(_client.Endpoint,operation.Token);
            if(_client.Session is null){Feedback("服务已连接，原房间已失效，请创建或加入新房间。",FeedbackTone.Warning);return;}
            if(host && !_usingCommunity && !_usingLan && ActiveNetworkState.Status=="error")await EstablishPublic();
            if(host && ActiveNetworkState.Status=="error")Feedback("房间已重新连接，公网尚未恢复，请查看连接提示。",FeedbackTone.Warning);
            else Feedback("房间已重新连接，成员列表已更新。");
        }catch(OperationCanceledException) when(operation.IsCancellationRequested){Feedback("已取消重新连接。",FeedbackTone.Info);}
        finally{_networkOperation=null;CancelNetworkButton.Visibility=Visibility.Collapsed;}
    }
    private async Task CloseOrLeaveRoom()
    {
        var host=IsRoomHost;var endpoint=_client.Endpoint;var confirmed=false;var serviceStopped=false;var cleanupFailed=false;var unconfirmed=0;
        InvalidateReadiness();_scheduler.RoomExited();
        Feedback(host?"正在关闭房间…":"正在退出房间…",FeedbackTone.Info);
        try {
            using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if(!_client.Connected && _client.Session is not null)await _client.ConnectAsync(endpoint,limit.Token);
            if(_client.Session is not null){await _client.LeaveRoomAsync(host,limit.Token);confirmed=true;unconfirmed=_client.UnconfirmedRoomExits;}
        }catch(Exception error){AppFaultLog.Record(error,"room-exit-confirmation");}
        finally {
            await Clean(()=>_client.DisconnectAsync(),"room-exit-disconnect");
            if(host && !confirmed && Uri.TryCreate(endpoint,UriKind.Absolute,out var address) && address.IsLoopback && address.Port==3132)
                try{serviceStopped=_server.StopOwned();}catch(Exception error){cleanupFailed=true;AppFaultLog.Record(error,"room-exit-service");}
            await Clean(()=>_tunnel.StopAsync(),"room-exit-tunnel");
            if(!_client.Connected)_client.ForgetDisconnectedRoom();
            _usingLan=false;_lanEndpoint=null;_usingCommunity=false;RenderNetwork(ActiveNetworkState);
        }
        if(cleanupFailed)Feedback("本机退出已处理，部分连接清理未完成，请关闭工具后重新打开。",FeedbackTone.Warning);
        else if(host && confirmed && unconfirmed>0)Feedback($"房间已关闭，旧邀请已失效。{unconfirmed} 位成员暂未确认退出，恢复连接后将离开旧房间。",FeedbackTone.Warning);
        else if(host && confirmed)Feedback("房间已关闭，所有在线成员已确认退出，旧邀请已失效。");
        else if(host && serviceStopped)Feedback("本机房间服务和公网连接已关闭，旧房间已失效。");
        else if(host)Feedback("已退出本机房间并关闭公网连接；尚未收到服务端关房确认。",FeedbackTone.Warning);
        else Feedback(confirmed?"已退出房间。":"已退出本机房间；尚未收到服务端退出确认。",confirmed?FeedbackTone.Success:FeedbackTone.Warning);
        async Task Clean(Func<Task> action,string stage){try{await action();}catch(Exception error){cleanupFailed=true;AppFaultLog.Record(error,stage);}}
    }
}
