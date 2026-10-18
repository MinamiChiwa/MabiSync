using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace MabiSync.App;

public partial class MainWindow
{
    private readonly WindowCapturePolicy _windowCapturePolicy=new();
    private bool _hideFromCapture;
    private Window? _sponsorWindow;

    private void InitializeWindowActions()
    {
        VersionText.Text=$"v{ApplicationVersion}";
        Title=$"MabiSync · v{ApplicationVersion}";
        InitializeUpdates();
        Topmost=true;
        RefreshWindowActions();
        SourceInitialized+=(_,_)=>ApplyCapturePolicy();
    }
    private void RefreshWindowActions()
    {
        SetHeaderToggle(PinWindowButton,Topmost,Topmost?"置顶已开启；点击取消置顶":"置顶已关闭；点击置顶");
        SetHeaderToggle(CapturePrivacyButton,_hideFromCapture,
            (_hideFromCapture?"录屏隐藏已开启；点击关闭":"录屏隐藏已关闭；点击开启")+"。开启后可能导致 NVIDIA 录制无法启动，遇到时请关闭此项再重试。需要 Windows 10 2004 或更高版本及兼容的采集软件。");
    }
    private void SetHeaderToggle(Button button,bool enabled,string hint)
    {
        button.Background=ColorBrush(enabled?"#244E73":"#29313E");
        button.Foreground=ColorBrush(enabled?"#A8E2FF":"#8C98AA");
        button.BorderBrush=ColorBrush(enabled?"#5798C6":"#46505E");
        button.ToolTip=hint;
        AutomationProperties.SetName(button,hint);
        AutomationProperties.SetItemStatus(button,enabled?"已开启":"已关闭");
    }
    private void PinWindowClick(object sender,RoutedEventArgs e)
    {
        Topmost=!Topmost;
        foreach(Window owned in OwnedWindows)owned.Topmost=Topmost;
        RefreshWindowActions();
        Feedback(Topmost?"窗口已置顶。":"已取消窗口置顶。",FeedbackTone.Info);
    }
    private void CapturePrivacyClick(object sender,RoutedEventArgs e)
    {
        var previous=_hideFromCapture;
        _hideFromCapture=!previous;
        if(!TryApplyCapturePolicy(out var error)) {
            _hideFromCapture=previous;
            TryApplyCapturePolicy(out _);
            Feedback(error!,FeedbackTone.Warning);
        }else {
            Feedback(_hideFromCapture?"已开启录屏隐藏。若 NVIDIA 无法开始录制，请关闭此项。":"已关闭录屏隐藏，可重试 NVIDIA 录制。",FeedbackTone.Info);
            SaveSettings();
        }
        RefreshWindowActions();
    }
    private bool TryApplyCapturePolicy(out string? error)
    {
        if(!_windowCapturePolicy.Apply(this,_hideFromCapture,out error))return false;
        foreach(Window owned in OwnedWindows)
            if(!_windowCapturePolicy.Apply(owned,_hideFromCapture,out error))return false;
        return true;
    }
    private void ApplyCapturePolicy()
    {
        if(!TryApplyCapturePolicy(out var error)) {
            _hideFromCapture=false;
            TryApplyCapturePolicy(out _);
            Feedback(error!,FeedbackTone.Warning);
            SaveSettings();
        }
        RefreshWindowActions();
    }
    private void ConfigureAuxiliaryWindow(Window window)
    {
        window.Icon=Icon;
        window.Topmost=Topmost;
        window.SourceInitialized+=(_,_)=>{
            if(!_windowCapturePolicy.Apply(window,_hideFromCapture,out var error))Feedback(error!,FeedbackTone.Warning);
        };
    }
    private void SponsorClick(object sender,RoutedEventArgs e)
    {
        if(_sponsorWindow is not null){_sponsorWindow.Activate();return;}
        var panel=new StackPanel{Margin=new Thickness(24)};
        panel.Children.Add(new TextBlock{Text="支持 MabiSync",FontSize=22,FontWeight=FontWeights.SemiBold});
        panel.Children.Add(new TextBlock{Text="如果工具对你有帮助，欢迎支持后续开发。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,6)});
        panel.Children.Add(new TextBlock{Text="赞助自愿，所有功能均可正常使用。",Foreground=ColorBrush("#A8B4C7"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,18)});
        var providers=new System.Collections.Generic.Dictionary<string,string>{["爱发电"]="https://afdian.com/a/minamichiwa",["Patreon"]="https://patreon.com/chiwaminami"};
        foreach(var (name,url) in providers) {
            var button=new Button{Content=name,HorizontalContentAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,4,0,6),Style=(Style)FindResource("PrimaryAction"),ToolTip="在浏览器中打开 "+name};
            button.Click+=(_,_)=>{
                try{Process.Start(new ProcessStartInfo(url){UseShellExecute=true});Feedback("已打开 "+name+"。",FeedbackTone.Info);}
                catch(Exception error) when(error is System.ComponentModel.Win32Exception or InvalidOperationException){Feedback("浏览器未能打开，请访问："+url,FeedbackTone.Warning);}
            };
            panel.Children.Add(button);
        }
        _sponsorWindow=new Window{Title="支持 MabiSync",Owner=this,Width=400,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false,Background=ColorBrush("#1A2331"),FontSize=14,FontFamily=FontFamily,Content=panel};
        ConfigureAuxiliaryWindow(_sponsorWindow);
        _sponsorWindow.Closed+=(_,_)=>_sponsorWindow=null;
        _sponsorWindow.Show();
    }
}
