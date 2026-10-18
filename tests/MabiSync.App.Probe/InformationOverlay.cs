using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using MabiSync.Client;
using MabiSync.Core;

internal static partial class Program
{
    private static async Task InformationOverlay()
    {
        var main=NewWindow(); var client=Field<RoomClient>(main,"_client");
        try
        {
            Check(!main.Topmost,"Main window is pinned by default");
            Check(main.FindName("InformationOverlayCheck") is CheckBox {IsChecked:false},"Overlay must be available and off by default");
            var toggle=(CheckBox)main.FindName("InformationOverlayCheck"); toggle.IsChecked=true;
            var overlay=Field<Window>(main,"_informationWindow");
            Check(overlay.IsVisible && overlay.Topmost && overlay.Owner is null && !overlay.ShowActivated,"Overlay must stay visible when main window minimizes without stealing initial focus");
            Check(((TextBlock)overlay.FindName("ScoreText")).Text.Contains("需要总指挥开启抓包功能"),"Missing capture instruction");
            var opacity=(Slider)overlay.FindName("OpacitySlider"); opacity.Value=.6;
            Check(Math.Abs(overlay.Opacity-.6)<.001,"Opacity slider does not change window opacity");
            ((Button)main.FindName("PinWindowButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)main.FindName("PinWindowButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(overlay.Topmost && !main.Topmost,"Main pin toggle changed the overlay pin");

            typeof(RoomClient).GetField("_session",Private)!.SetValue(client,new SessionIdentity("FIXTUR","host","fixture","epoch","ws://127.0.0.1:3132/"));
            typeof(RoomClient).GetField("_connected",Private)!.SetValue(client,true);
            SetSnapshot(client,["host","member"]);
            var nodes=JsonNode.Parse(client.Room!.Value.GetRawText())!;
            var now=Field<StopwatchClock>(main,"_clock").NowMs;
            Field<RoomClock>(main,"_roomClock").AddSample(now,now,now,now);
            nodes["mode"]="ensemble";
            nodes["members"]![0]!["ensembleGroup"]=2;nodes["members"]![0]!["ensembleOrder"]=1;
            nodes["members"]![0]!["instrument"]="钢琴";
            nodes["members"]![0]!["music"]=JsonSerializer.SerializeToNode(new {title="Fixture score",concertId="7",scoreSha256="fixture",wholeScoreDurationSeconds=60,selectedDurationSeconds=45,estimatedStartServerMs=now-5000,issue="Fixture estimate"});
            nodes["members"]![0]!["gameIdentity"]=JsonSerializer.SerializeToNode(new {characterId="101",realmId=3,nickname="Fixture"});
            nodes["members"]![1]!["ensembleGroup"]=1;nodes["members"]![1]!["ensembleOrder"]=2;
            void Publish()
            {
                var type=typeof(RoomClient).GetNestedType("Snapshot",BindingFlags.NonPublic)!;
                typeof(RoomClient).GetField("_snapshot",Private)!.SetValue(client,Activator.CreateInstance(type,[JsonSerializer.SerializeToElement(nodes)]));
                Call(main,"Refresh");
            }
            Publish();
            Check(((TextBlock)overlay.FindName("ScoreText")).Text.Contains("Fixture score"),"Overlay did not update score");
            Check(((Button)overlay.FindName("StartButton")).Visibility==Visibility.Visible && ((Button)overlay.FindName("ReadyButton")).Visibility==Visibility.Collapsed,"Overlay host actions wrong");
            var rows=((ItemsControl)overlay.FindName("MembersList")).Items.Cast<object>().ToArray();
            Check((string)rows[0].GetType().GetProperty("Id")!.GetValue(rows[0])! == "member","Overlay roster is not ordered by group and slot");
            nodes["members"]![1]!["ready"]=true; nodes["members"]![1]!["instrument"]="长笛";Publish();
            Check((bool)rows[0].GetType().GetProperty("Ready")!.GetValue(rows[0])! && (string)rows[0].GetType().GetProperty("Instrument")!.GetValue(rows[0])! == "长笛","Overlay member status/instrument is stale");
            typeof(RoomClient).GetField("_session",Private)!.SetValue(client,new SessionIdentity("FIXTUR","member","fixture","epoch","ws://127.0.0.1:3132/"));Publish();
            Check(((Button)overlay.FindName("ReadyButton")).Content.Equals("已就绪 · 取消") && ((Button)overlay.FindName("StartButton")).Visibility==Visibility.Collapsed,"Overlay member role switch stale");
            nodes["performance"]=JsonSerializer.SerializeToNode(new {id="overlay-round",phase="performing",mode="ensemble",leaderId="host",participantIds=new[]{"host","member"},earliestStartServerMs=now-5000,startAction=new{id="overlay-start",targetServerMs=now-5000},stopAction=new{id="overlay-stop",targetServerMs=now+40000},stopCommitted=true,stopRule="first-cycle",cycleScore=new {concertId="7",scoreSha256="fixture",durationSeconds=60},interruption=(object?)null,receipts=Array.Empty<object>()});Publish();
            var progress=(ProgressBar)overlay.FindName("ScoreProgress");var before=progress.Value;
            await Task.Delay(150);Call(main,"RefreshStatus");
            Check(progress.Visibility==Visibility.Visible && progress.Value>before && ((TextBlock)overlay.FindName("ProgressText")).Text.Contains("秒"),"Overlay progress is stale or missing");
            var performing=nodes["performance"]!.DeepClone();
            nodes["performance"]!["phase"]="preparing";nodes["performance"]!["earliestStartServerMs"]=now+2000;nodes["performance"]!["startAction"]!["targetServerMs"]=now+2000;Publish();
            Check(progress.Visibility==Visibility.Collapsed,"New round countdown displays the old score's progress");
            nodes["performance"]!["phase"]="performing";Publish();
            Check(progress.Visibility==Visibility.Collapsed,"Old onset passed current round freshness check");
            nodes["performance"]=performing.DeepClone();nodes["performance"]!["cycleScore"]!["concertId"]="8";Publish();
            Check(progress.Visibility==Visibility.Collapsed,"Wrong concert animated a bound first-cycle round");
            nodes["performance"]=performing.DeepClone();nodes["members"]![0]!["music"]!["estimatedStartServerMs"]=null;Publish();
            Check(progress.Visibility==Visibility.Collapsed && ((TextBlock)overlay.FindName("ScoreText")).Text.Contains("Fixture score") && ((TextBlock)main.FindName("ScoreObservationText")).Text.Contains("60") && ((TextBlock)overlay.FindName("ProgressText")).Text.Contains("等待本轮起点"),"Missing onset hid valid parsed score details or animated unconfirmed progress");
            nodes["members"]![0]!["music"]!["estimatedStartServerMs"]=now-5000;
            nodes["performance"]=performing;Publish();
            var images=Environment.GetEnvironmentVariable("MABISYNC_UI_OUTPUT");
            ((TextBlock)main.FindName("MessageText")).Text="";Call(main,"RefreshStatus");
            overlay.SizeToContent=SizeToContent.Manual;
            Layout(overlay,380,400);
            var sliderRect=opacity.TransformToAncestor((System.Windows.Media.Visual)overlay.Content).TransformBounds(new Rect(0,0,opacity.ActualWidth,opacity.ActualHeight));
            Check(sliderRect.Bottom<=400,"Compact overlay clips its opacity control");
            if(images is not null){Directory.CreateDirectory(images);Render(overlay,460,420,Path.Combine(images,"information-overlay.png"));Render(overlay,380,400,Path.Combine(images,"information-overlay-compact.png"));Render(main,780,700,Path.Combine(images,"overlay-entry.png"));}
            var membersSection=(Expander)overlay.FindName("MembersSection");
            var membersContent=(FrameworkElement)membersSection.Content;
            overlay.SizeToContent=SizeToContent.Height;overlay.UpdateLayout();await Task.Delay(50);
            var expandedHeight=overlay.ActualHeight;
            overlay.SizeToContent=SizeToContent.Manual;
            membersSection.IsExpanded=false;overlay.UpdateLayout();await Task.Delay(50);
            Check(!membersContent.IsVisible && overlay.SizeToContent==SizeToContent.Height && overlay.ActualHeight<expandedHeight-50,"Collapsing roster must hide headers/rows and shrink a manually resized overlay");
            Check(((TextBlock)overlay.FindName("ScoreText")).IsVisible && opacity.IsVisible && ((Button)overlay.FindName("ReadyButton")).IsVisible,"Collapsed roster hid score or controls");
            var collapsedHeight=overlay.ActualHeight;
            if(images is not null)Render(overlay,380,(int)Math.Ceiling(collapsedHeight),Path.Combine(images,"information-overlay-collapsed.png"));
            nodes["members"]![1]!["instrument"]="小提琴";Publish();
            membersSection.IsExpanded=true;overlay.UpdateLayout();await Task.Delay(50);
            Check(membersContent.IsVisible && overlay.ActualHeight>collapsedHeight+50 && (string)rows[0].GetType().GetProperty("Instrument")!.GetValue(rows[0])! == "小提琴","Expanding roster must restore height and show changes received while collapsed");
            nodes["members"]![0]!["music"]=null;nodes["members"]![0]!["gameIdentity"]=null;nodes["performance"]=null;Publish();
            Check(progress.Visibility==Visibility.Collapsed && ((TextBlock)overlay.FindName("ScoreText")).Text.Contains("需要总指挥开启抓包功能"),"Overlay kept removed score/progress");
            var capture=Field<object>(main,"_gameObservation");
            var observed=new GameObservation("fixture",1,new GameIdentity(101,3,"Local",11),null,"已识别角色昵称");
            void Capture(GameObservation state,string status)=>capture.GetType().GetMethod("Publish",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(capture,[Activator.CreateInstance(capture.GetType().Assembly.GetType("MabiSync.Windows.CaptureObservation")!,[state,5L,true,status])]);
            Capture(observed,observed.Detail);
            Call(main,"ObservationTick");Check(((TextBlock)main.FindName("ObservationStatusText")).Foreground.ToString()=="#FF86E1B2","Capture success is not green");
            Capture(observed with{Identity=null},"等待识别");
            Call(main,"ObservationTick");Check(((TextBlock)main.FindName("ObservationStatusText")).Foreground.ToString()!="#FF86E1B2","Lost capture stayed green");
            ((Button)overlay.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(toggle.IsChecked==false && !overlay.IsVisible,"Closing overlay left stale main toggle");
            toggle.IsChecked=true;Check(!ReferenceEquals(overlay,Field<Window>(main,"_informationWindow")),"Closed overlay cannot reopen");
            Console.WriteLine("PASS WPF overlay live roster, roles, progress, opacity, capture feedback, close and reopen");
        }
        finally {typeof(RoomClient).GetField("_connected",Private)!.SetValue(client,false);await Close(main);}
        Check(Field<object?>(main,"_informationWindow") is null,"Main close left an orphan overlay");
    }
    private static async Task OverlayCommands(string endpoint)
    {
        var host=NewWindow();var member=NewWindow();
        var hc=Field<RoomClient>(host,"_client");var mc=Field<RoomClient>(member,"_client");
        try
        {
            await hc.ConnectAsync(endpoint);await hc.CreateRoomAsync("Overlay host");await hc.SetRoomModeAsync("emote");
            await mc.ConnectAsync(endpoint);await mc.JoinRoomAsync("Overlay member",hc.Session!.Code);
            await Wait(()=>mc.Room?.GetProperty("members").GetArrayLength()==2);Call(host,"Refresh");Call(member,"Refresh");
            ((CheckBox)host.FindName("InformationOverlayCheck")).IsChecked=true;((CheckBox)member.FindName("InformationOverlayCheck")).IsChecked=true;
            var ho=Field<Window>(host,"_informationWindow");var mo=Field<Window>(member,"_informationWindow");
            ((Button)mo.FindName("ReadyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)mo.FindName("ReadyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>hc.Room?.GetProperty("members").EnumerateArray().Any(m=>m.GetProperty("id").GetString()==mc.Session!.MemberId && m.GetProperty("ready").GetBoolean())==true);
            Call(host,"Refresh");Call(member,"Refresh");
            Check(((Button)mo.FindName("ReadyButton")).Content.Equals("已就绪 · 取消") && ((TextBlock)ho.FindName("PhaseText")).Text=="全员已就绪","Live overlay readiness did not reach both computers");
            ((TextBox)host.FindName("CountdownBox")).Text="1";
            ((Button)ho.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)ho.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>mc.Room?.GetProperty("performance").ValueKind==JsonValueKind.Object);
            await Wait(()=>mc.Room?.GetProperty("performance").GetProperty("phase").GetString()=="stopped");
            Call(host,"Refresh");Call(member,"Refresh");
            Check(Equals(((Button)mo.FindName("ReadyButton")).Content,((Button)member.FindName("ReadyButton")).Content),"Emote completion left different main/overlay readiness");
            await Wait(()=>((System.Collections.ICollection)Field<object>(host,"_receipts")).Count>=1 && ((System.Collections.ICollection)Field<object>(member,"_receipts")).Count>=1);
            Check(((System.Collections.ICollection)Field<object>(host,"_receipts")).Count==1 && ((System.Collections.ICollection)Field<object>(member,"_receipts")).Count==1,"Overlay issued duplicate input");
            await hc.LeaveRoomAsync(true);await Wait(()=>mc.Session is null);Call(member,"Refresh");
            Check(((ItemsControl)mo.FindName("MembersList")).Items.Count==0 && ((Button)mo.FindName("ReadyButton")).Visibility==Visibility.Collapsed,"Closed room left stale overlay members or active actions");
            Console.WriteLine("PASS WPF live overlay commands share readiness/start, ignore double clicks and clear closed rooms; desktop inputs: 0");
        }
        finally {await Close(member);await Close(host);}
    }
}
