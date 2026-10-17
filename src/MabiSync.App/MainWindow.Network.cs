using System.IO;
using System.Windows;
using System.Windows.Controls;
using MabiSync.Client;
using MabiSync.Core;

namespace MabiSync.App;

public partial class MainWindow
{
    CancellationTokenSource? _networkOperation;
    bool _usingLan;
    Uri? _lanEndpoint;
    bool _usingCommunity;
    Uri _communityEndpoint=new("wss://47.111.3.134/");
    string SelectedProvider=>(PublicProviderBox.SelectedItem as ComboBoxItem)?.Tag as string??"cloudflare";
    TunnelState ActiveNetworkState=>_usingCommunity
        ? new(ViewSession is null?"off":ViewConnected?"ready":"error",_communityEndpoint,
            ViewSession is null?"服务器直连（非长期）":ViewConnected?"服务器直连已连接 · 非长期服务":"服务器连接已断开，请重新连接。")
        :_usingLan ? new(ViewSession is null?"off":ViewConnected?"ready":"error",_lanEndpoint,ViewSession is null?"局域网":ViewConnected?"局域网房间已连接":"局域网连接已断开，请重新连接。") :_tunnel.State;
    bool ShareReady=>ActiveNetworkState.Status=="ready";
    string? CurrentInvitation=>ViewSession is{} session && ShareReady && ActiveNetworkState.Endpoint is{} endpoint?new RoomInvitation(endpoint,session.Code).ToString():null;
    void PublicProviderChanged(object sender,SelectionChangedEventArgs e)
    {
        if(!_uiReady || _closing)return;
        RefreshProvider();SaveSettings();
    }
    void RefreshProvider()
    {
        PublicProviderHint.Text=SelectedProvider switch {
            "community"=>$"服务器直连（非长期）：{_communityEndpoint.Authority}。可创建房间，或输入六位房间码加入；服务可用期间使用。",
            "lan"=>"局域网：连接同一路由器，或先用组网软件互通。房主建房后分享邀请；成员也可填写房主地址与房间码加入。",
            _=>"Cloudflare：创建临时公网地址，好友粘贴邀请即可加入。"};
        if(ViewSession is not null && !IsRoomHost)PublicProviderHint.Text="已使用房主邀请中的服务地址。";
        if(_usingCommunity && ViewSession is not null)PublicProviderHint.Text=$"服务器直连（非长期）· {_communityEndpoint.Authority}";
        InvitationBox.ToolTip=SelectedProvider!="cloudflare"?"输入六位房间码，或粘贴完整邀请":"粘贴房主复制的完整邀请";
        JoinInvitationButton.Content=SelectedProvider!="cloudflare"?"加入房间":"加入邀请";
        InvitationHint.Text=SelectedProvider!="cloudflare"?"加入方：输入六位房间码，或粘贴完整邀请。":"加入方：将完整邀请粘贴到上框，点击“加入邀请”。";
        LanControls.Visibility=SelectedProvider=="lan"?Visibility.Visible:Visibility.Collapsed;
        QuickCreateButton.Content=SelectedProvider=="lan"?"创建房间":"创建公网房间";
        PublicProviderBox.IsEnabled=ViewSession is null && !_connectionBusy;
        LanAddressBox.IsEnabled=ViewSession is null && !_connectionBusy;
        OpenPublicButton.IsEnabled=OpenPublicButton.IsEnabled && SelectedProvider=="cloudflare" && !_usingCommunity && !_usingLan;
    }
    async void CancelNetworkClick(object sender,RoutedEventArgs e)
    {
        _networkOperation?.Cancel();CancelNetworkButton.IsEnabled=false;
        try {await StopNetworks();Feedback("公网连接已取消。");}
        catch(Exception error){Feedback("关闭连接失败："+error.Message,FeedbackTone.Error);}
        finally{CancelNetworkButton.IsEnabled=true;}
    }
    Task StopNetworks()=>_tunnel.StopAsync();
    async Task ConnectCommunity(bool create,string? code=null)
    {
        RequireNoRoom();var nickname=RoomNickname();
        if(!create && (code is null || code.Length!=6 || !code.All(char.IsAsciiLetterOrDigit)))
            throw new FormatException("请输入六位房间码，或粘贴完整邀请。");
        await StopNetworks();_usingLan=false;_usingCommunity=true;
        EndpointBox.Text=_communityEndpoint.AbsoluteUri;
        using var operation=new CancellationTokenSource(TimeSpan.FromSeconds(15));_networkOperation=operation;
        CancelNetworkButton.Visibility=Visibility.Visible;
        Feedback("正在连接服务器…",FeedbackTone.Info);
        try {
            await _client.ConnectAsync(EndpointBox.Text,operation.Token);
            if(create)await CreateConfiguredRoom(nickname,operation.Token);
            else await _client.JoinRoomAsync(nickname,code!,operation.Token);
            InvitationBox.Clear();SaveSettings();RenderNetwork(ActiveNetworkState);
            Feedback(create?"房间已创建，复制邀请给好友。":"已加入房间，请确认按键或按钮设置后点击“就绪”。");
        }catch(Exception error){
            if(_client.Session is null){await _client.DisconnectAsync();_client.ForgetDisconnectedRoom();_usingCommunity=false;}
            if(error is OperationCanceledException)throw new IOException("服务器连接已取消或超时，请重试。",error);
            throw new IOException("服务器连接未完成："+Friendly(error.Message)+"。该入口为非长期服务，可选择其他联机方式。",error);
        }finally{_networkOperation=null;CancelNetworkButton.Visibility=Visibility.Collapsed;}
    }
    async Task EstablishPublic()
    {
        if(_usingCommunity || SelectedProvider=="community")throw new InvalidOperationException("服务器直连请使用“创建公网房间”，或填写房间码加入。");
        if(SelectedProvider=="lan" || _usingLan)throw new InvalidOperationException("局域网房间可直接分享邀请；公网连接请选择 Cloudflare。");
        await StopNetworks();_usingCommunity=false;
        using var operation=new CancellationTokenSource();_networkOperation=operation;
        CancelNetworkButton.Visibility=Visibility.Visible;
        try {
            var executable=Path.Combine(AppContext.BaseDirectory,"runtime","cloudflared.exe");
            if(!File.Exists(executable))throw new IOException("缺少 Cloudflare 组件，请重新解压运行包。");
            await _tunnel.StartAsync(executable,token:operation.Token);
        }finally{_networkOperation=null;CancelNetworkButton.Visibility=Visibility.Collapsed;}
    }
    void RenderNetwork(TunnelState state)
    {
        PublicStateText.Text=state.Message;
        PublicAddressBox.Text=state.Endpoint?.AbsoluteUri??"";
        NetworkBanner.Visibility=state.Status is "off" or "ready"?Visibility.Collapsed:Visibility.Visible;
        NetworkBanner.Background=ColorBrush(state.Status switch{"ready"=>"#173D32","error"=>"#482730",_=>"#203552"});
        NetworkBannerText.Foreground=ColorBrush(state.Status switch{"ready"=>"#86E1B2","error"=>"#FFAFB9",_=>"#9DC7FF"});
        NetworkBannerText.Text=state.Message;
    }
}
