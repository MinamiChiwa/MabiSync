using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using MabiSync.Client;

namespace MabiSync.App;

public partial class MainWindow
{
    private enum FeedbackTone {Info,Success,Warning,Error}
    private string? _displayedFeedback;
    private FeedbackTone _feedbackTone;
    private void Feedback(string text,FeedbackTone tone=FeedbackTone.Success)
    {MessageText.Text=text;_feedbackTone=tone;_displayedFeedback=text;RefreshFeedback();}
    private void RefreshFeedback()
    {
        var text=MessageText.Text;
        if(_displayedFeedback!=text) {
            _displayedFeedback=text;
            _feedbackTone=text.Contains("失败")||text.Contains("错误")||text.Contains("无法")||text.Contains("未能")||text.Contains("中断")||text.Contains("不匹配")?FeedbackTone.Error:
                text.StartsWith("已")||text.Contains("成功")?FeedbackTone.Success:FeedbackTone.Info;
        }
        FeedbackBanner.Visibility=string.IsNullOrWhiteSpace(text)?Visibility.Collapsed:Visibility.Visible;
        var (background,foreground,label)=_feedbackTone switch {
            FeedbackTone.Success=>("#173D32","#86E1B2","操作完成"),FeedbackTone.Error=>("#482730","#FFAFB9","需要处理"),
            FeedbackTone.Warning=>("#44391F","#F6D58B","请留意"),_=>("#203552","#9DC7FF","操作反馈")
        };
        FeedbackBanner.Background=ColorBrush(background);MessageText.Foreground=FeedbackKindText.Foreground=ColorBrush(foreground);FeedbackKindText.Text=label;
    }
    private static Brush ColorBrush(string value){var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));brush.Freeze();return brush;}
    private void TunnelChanged(TunnelState state)
    {
        if(_closing || _usingLan || _usingCommunity || !ReferenceEquals(state,_tunnel.State))return;
        PublicStateText.Text=state.Message;PublicAddressBox.Text=state.Endpoint?.AbsoluteUri??"";
        NetworkBanner.Visibility=state.Status is "off" or "ready"?Visibility.Collapsed:Visibility.Visible;
        NetworkBanner.Background=ColorBrush(state.Status switch{"ready"=>"#173D32","error"=>"#482730",_=>"#203552"});
        NetworkBannerText.Foreground=ColorBrush(state.Status switch{"ready"=>"#86E1B2","error"=>"#FFAFB9",_=>"#9DC7FF"});
        NetworkBannerText.Text=state.Status=="ready"?"公网房间已就绪 · 点击“复制邀请”分享给好友。":state.Message;
        if(state.Status=="ready")Feedback("公网连接已建立，邀请已可分享。");
        else if(state.Status=="error")Feedback(state.Message,FeedbackTone.Error);
        else if(state.Status is "starting" or "verifying")Feedback(state.Message,FeedbackTone.Info);
        Refresh();
    }
    private void RefreshCountdown(JsonElement? round)
    {
        CountdownPanel.Visibility=Visibility.Collapsed;CountdownProgress.IsIndeterminate=false;
        if(round is not{ValueKind:JsonValueKind.Object} p || p.GetProperty("phase").GetString() is "cancelled" or "stopped")return;
        var now=_roomClock.ServerNowMs;var start=p.GetProperty("startAction");
        var startTarget=start.GetProperty("targetServerMs").GetDouble();
        var startRemaining=(_scheduler.LocalDeadline(start.GetProperty("id").GetString()!) is{} deadline?deadline-_clock.NowMs:startTarget+TimingOffsetMs-(now??startTarget));
        double remaining,total;bool stopping;
        if(startRemaining>0 && p.GetProperty("phase").GetString() is "preparing" or "committed") {
            stopping=false;remaining=startRemaining;total=p.TryGetProperty("countdownMs",out var ms)?ms.GetDouble():5000;
        } else if(p.TryGetProperty("stopAction",out var stop)&&stop.ValueKind==JsonValueKind.Object && (!p.TryGetProperty("stopCommitted",out var committed)||committed.GetBoolean())) {
            stopping=true;remaining=_scheduler.LocalDeadline(stop.GetProperty("id").GetString()!) is{} stopDeadline?stopDeadline-_clock.NowMs:stop.GetProperty("targetServerMs").GetDouble()+TimingOffsetMs-(now??startTarget);
            total=Math.Max(1,stop.GetProperty("targetServerMs").GetDouble()-startTarget);
        } else return;
        CountdownPanel.Visibility=Visibility.Visible;
        CountdownLabel.Text=stopping?"距离统一停止":"距离同步开始";
        CountdownProgress.IsIndeterminate=now is null;CountdownProgress.Foreground=ColorBrush(stopping?"#67CF9D":"#79ACFF");
        CountdownProgress.Value=Math.Clamp(remaining/Math.Max(1,total),0,1);
        CountdownValue.Text=now is null?"正在校时":$"{Math.Max(0,remaining/1000):0.0} 秒";
    }
}
