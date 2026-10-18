using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace MabiSync.App;

public partial class MainWindow
{
    private InformationWindow? _informationWindow;
    private double _informationOpacity=.9;
    private readonly ObservableCollection<MemberRow> _informationMembers=[];
    private void InformationOverlayChanged(object sender,RoutedEventArgs e)
    {
        if(!_uiReady || _closing)return;
        if(InformationOverlayCheck.IsChecked!=true){_informationWindow?.Close();return;}
        if(_informationWindow is not null)return;
        var overlay=new InformationWindow{Icon=Icon};
        overlay.MaxHeight=Math.Max(320,Math.Min(640,SystemParameters.WorkArea.Height-40));
        overlay.OpacitySlider.Value=_informationOpacity;
        overlay.MembersList.ItemsSource=_informationMembers;
        overlay.ReadyRequested=()=>ForwardInformationAction(ReadyButton);
        overlay.StartRequested=()=>ForwardInformationAction(StartButton);
        overlay.OpacityUpdated=value=>{_informationOpacity=value;SaveSettings();};
        overlay.SourceInitialized+=(_,_)=>{
            if(!_windowCapturePolicy.Apply(overlay,_hideFromCapture,out var error))Feedback(error!,FeedbackTone.Warning);
        };
        overlay.Closed+=(_,_)=>{
            _informationWindow=null;_informationMembers.Clear();InformationOverlayCheck.IsChecked=false;
            if(!_closing)Feedback("信息浮窗已关闭。",FeedbackTone.Info);
        };
        _informationWindow=overlay;
        overlay.Left=Math.Clamp(Left+Width-overlay.Width-20,SystemParameters.WorkArea.Left,Math.Max(SystemParameters.WorkArea.Left,SystemParameters.WorkArea.Right-overlay.Width));
        overlay.Top=Math.Clamp(Top+50,SystemParameters.WorkArea.Top,Math.Max(SystemParameters.WorkArea.Top,SystemParameters.WorkArea.Bottom-overlay.MaxHeight));
        Refresh();overlay.Show();
        Feedback("信息浮窗已打开。",FeedbackTone.Info);
    }
    private void ForwardInformationAction(Button source)
    {
        if(_closing || !source.IsEnabled || source.Visibility!=Visibility.Visible)return;
        source.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        RefreshStatus();
    }
    private void RefreshInformationWindow()
    {
        if(_informationWindow is not{} overlay)return;
        var ordered=_members.OrderBy(m=>SelectedMode=="ensemble"?m.Group:0).ThenBy(m=>SelectedMode=="ensemble"?m.Order:0).ToArray();
        for(var i=0;i<ordered.Length;i++)
        {
            var old=_informationMembers.IndexOf(ordered[i]);
            if(old<0)_informationMembers.Insert(i,ordered[i]);
            else if(old!=i)_informationMembers.Move(old,i);
            ordered[i].UpdateOverlayRole(ViewRoom?.GetProperty("leaderId").GetString()==ordered[i].Id);
        }
        while(_informationMembers.Count>ordered.Length)_informationMembers.RemoveAt(_informationMembers.Count-1);
        overlay.RoomText.Text=ViewSession is{} session?$"房间 {session.Code} · {(ViewConnected?"已连接":"连接中断")}":"尚未加入房间";
        overlay.PhaseText.Text=PhaseText.Text;overlay.PhaseText.Foreground=PhaseText.Foreground;
        overlay.FeedbackText.Text=MessageText.Text;overlay.FeedbackText.Foreground=MessageText.Foreground;
        overlay.FeedbackText.Visibility=FeedbackBanner.Visibility;
        CopyAction(ReadyButton,overlay.ReadyButton);CopyAction(StartButton,overlay.StartButton);
        overlay.ScoreProgress.Visibility=Visibility.Collapsed;overlay.ProgressText.Text="";
        overlay.ScoreText.Text=SelectedMode=="emote"?"表情动作": "需要总指挥开启抓包功能";
        if(!ViewConnected || SelectedMode=="emote" || ViewRoom is not{} room)return;
        var performance=room.GetProperty("performance");
        var active=performance.ValueKind==JsonValueKind.Object && performance.GetProperty("phase").GetString() is not ("stopped" or "cancelled");
        var leader=active?performance.GetProperty("leaderId").GetString():room.GetProperty("leaderId").GetString();
        foreach(var member in room.GetProperty("members").EnumerateArray())
        {
            if(member.GetProperty("id").GetString()!=leader)continue;
            var identified=member.TryGetProperty("gameIdentity",out var identity) && identity.ValueKind==JsonValueKind.Object;
            if(!member.TryGetProperty("music",out var score) || score.ValueKind!=JsonValueKind.Object)
            {overlay.ScoreText.Text=identified?"等待总指挥开始演奏，识别乐谱":"需要总指挥开启抓包功能";return;}
            var title=score.GetProperty("title").GetString();
            overlay.ScoreText.Text=string.IsNullOrWhiteSpace(title)?"未命名乐谱":title;
            if(!score.TryGetProperty("wholeScoreDurationSeconds",out var duration) || duration.ValueKind!=JsonValueKind.Number)return;
            var seconds=duration.GetDouble();if(!double.IsFinite(seconds) || seconds<=0)return;
            overlay.ProgressText.Text=$"一轮 {seconds:0.###} 秒 · 等待演奏";
            if(active && !ScoreBelongsToRound(score,performance))
            {overlay.ProgressText.Text=$"一轮 {seconds:0.###} 秒 · 等待本轮起点";return;}
            if(!active || _roomClock.ServerNowMs is not{} now)return;
            double? start=score.TryGetProperty("estimatedStartServerMs",out var onset) && onset.ValueKind==JsonValueKind.Number?onset.GetDouble():null;
            if(start is null)return;
            var elapsed=(now-start.Value)/1000;
            if(elapsed<0){overlay.ProgressText.Text=$"一轮 {seconds:0.###} 秒 · 等待开始";return;}
            var cycle=1d;var position=elapsed;
            var ending=performance.TryGetProperty("stopRule",out var rule) && rule.GetString()=="first-cycle" && performance.TryGetProperty("stopAction",out var stop) && stop.ValueKind==JsonValueKind.Object && (!performance.TryGetProperty("stopCommitted",out var committed) || committed.GetBoolean());
            if(!ending){cycle=Math.Floor(elapsed/seconds)+1;position=elapsed%seconds;}
            overlay.ScoreProgress.Value=Math.Clamp(position/seconds,0,1);
            overlay.ScoreProgress.Visibility=Visibility.Visible;
            overlay.ProgressText.Text=$"{Math.Min(position,seconds):0.0} / {seconds:0.###} 秒 · 第 {cycle:0} 轮 · 估计进度";
            return;
        }
    }
    private static void CopyAction(Button source,Button target)
    {
        target.Content=source.Content;target.Style=source.Style;
        target.Visibility=source.Visibility;target.IsEnabled=source.IsEnabled;
    }
}
