using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using MabiSync.Core;

namespace MabiSync.App;

public partial class MainWindow
{
    static string[] LocalAddresses()
    {
        try {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n=>n.OperationalStatus==OperationalStatus.Up && n.NetworkInterfaceType!=NetworkInterfaceType.Loopback)
                .OrderByDescending(n=>n.GetIPProperties().GatewayAddresses.Count>0)
                .SelectMany(n=>n.GetIPProperties().UnicastAddresses)
                .Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                .Select(a=>a.Address.ToString()).Distinct().ToArray();
        }catch(NetworkInformationException){return [];}
    }
    void LoadLanAddresses()
    {
        foreach(var address in LocalAddresses())LanAddressBox.Items.Add($"ws://{address}:3132/");
        LanAddressBox.Text=LanAddressBox.Items.Count>0?LanAddressBox.Items[0].ToString():"ws://127.0.0.1:3132/";
    }
    Uri ReadLanEndpoint()
    {
        var text=LanAddressBox.Text.Trim();
        if(!text.Contains("://",StringComparison.Ordinal))text="ws://"+text;
        if(!Uri.TryCreate(text,UriKind.Absolute,out var endpoint) || endpoint.Scheme!="ws" ||
            !IPAddress.TryParse(endpoint.Host,out var ip) || ip.AddressFamily!=AddressFamily.InterNetwork ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || endpoint.AbsolutePath!="/" ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new FormatException("请填写房主的局域网地址，例如 ws://192.168.1.10:3132。");
        if(endpoint.IsDefaultPort)endpoint=new UriBuilder(endpoint){Port=3132}.Uri;
        return endpoint;
    }
    static bool IsLocalNetwork(Uri endpoint)
    {
        if(endpoint.Scheme!="ws" || !IPAddress.TryParse(endpoint.Host,out var ip))return false;
        var b=ip.GetAddressBytes();
        return IPAddress.IsLoopback(ip) || b.Length==4 && (b[0]==10 || b[0]==192 && b[1]==168 || b[0]==172 && b[1] is >=16 and <=31 || b[0]==100 && b[1] is >=64 and <=127);
    }
    async Task CreateLanRoom()
    {
        var nickname=RoomNickname();var endpoint=ReadLanEndpoint();
        if(endpoint.Port!=3132 || !LocalAddresses().Contains(endpoint.Host))
            throw new IOException("房主请在地址下拉框选择本机网卡地址。使用组网软件时，选择它分配给本机的地址。");
        await StopNetworks();_usingCommunity=false;_usingLan=true;_lanEndpoint=endpoint;
        try {
            await _server.StartAsync(true);
            EndpointBox.Text="ws://127.0.0.1:3132/";
            await _client.ConnectAsync(EndpointBox.Text);
            await CreateConfiguredRoom(nickname);
            SaveSettings();RenderNetwork(ActiveNetworkState);
            Feedback("局域网房间已创建，复制邀请发给同网络的好友。");
        }catch {
            if(_client.Session is null){await _client.DisconnectAsync();_usingLan=false;_lanEndpoint=null;}
            throw;
        }
    }
    Window? _lanTutorial;
    void LanTutorialClick(object sender,RoutedEventArgs e)
    {
        if(_lanTutorial is{} shown){shown.Activate();return;}
        var addresses=LocalAddresses();
        var text="房主\n1. 两台电脑先连接同一个路由器。异地玩家先用 Tailscale 等组网软件连通。\n2. 选择“局域网”，在地址框选择本机地址，点击“创建房间”。\n3. Windows 询问防火墙权限时，允许当前使用的网络。\n4. 点击“复制邀请”，发给好友。\n\n成员\n1. 粘贴房主的完整邀请，点击“加入房间”。\n2. 也可填写房主地址（如 ws://192.168.1.10:3132）和六位房间码加入。\n\n连接不上时\n确认双方在同一网络；关闭路由器的访客隔离。房主检查 Windows 防火墙是否允许 MabiSync 的 TCP 3132 端口。组网软件用户填写房主的虚拟网卡地址。\n\n本机可用地址\n"+
            (addresses.Length==0?"尚未检测到，请检查网络连接。":string.Join("\n",addresses.Select(a=>$"ws://{a}:3132")));
        var content=new StackPanel{Margin=new Thickness(22)};
        content.Children.Add(new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,FontSize=14});
        var close=new Button{Content="知道了",HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,16,0,0)};
        content.Children.Add(close);
        var area=SystemParameters.WorkArea;
        var guide=new Window{Title="局域网连接教程",Owner=this,Width=Math.Min(550,area.Width),Height=Math.Min(650,area.Height),WindowStartupLocation=WindowStartupLocation.CenterOwner,
            Background=Background,Foreground=Foreground,Content=new ScrollViewer{Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}};
        close.Click+=(_,_)=>guide.Close();guide.Closed+=(_,_)=>_lanTutorial=null;_lanTutorial=guide;ConfigureAuxiliaryWindow(guide);guide.Show();
        Feedback("已打开局域网连接教程。",FeedbackTone.Info);
    }
}
