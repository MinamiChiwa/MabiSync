using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MabiSync.Core;
using MabiSync.Windows;

namespace MabiSync.App;

/// <summary>A local overlay consumes the selection click. Coordinates come from Win32 physical pixels.</summary>
internal sealed class ButtonPickerWindow : Window
{
    private readonly WindowsGamePlatform _platform;
    private readonly GameWindowId _game;
    private readonly ClientArea _area;
    private readonly TaskCompletionSource<ButtonBinding?> _completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _check=new(){Interval=TimeSpan.FromMilliseconds(40)};
    private bool _leftDown;
    internal ButtonPickerWindow(WindowsGamePlatform platform,GameWindowId game,ClientArea area,string? stopShortcut=null)
    {
        _platform=platform;_game=game;_area=area;
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;AllowsTransparency=true;
        Background=new SolidColorBrush(Color.FromArgb(24,30,100,220));Topmost=true;ShowInTaskbar=false;ShowActivated=false;Cursor=Cursors.Cross;
        Width=100;Height=100;
        Content=new Border {BorderBrush=new SolidColorBrush(Color.FromRgb(62,130,245)),BorderThickness=new(2),Child=new Border {
            VerticalAlignment=VerticalAlignment.Top,HorizontalAlignment=HorizontalAlignment.Center,Margin=new(12),Padding=new(16,10,16,10),CornerRadius=new(7),Background=new SolidColorBrush(Color.FromArgb(240,24,39,64)),
            Child=new TextBlock{Text="点击要记录的游戏按钮 · 右键 / Esc"+(stopShortcut is null?"":" / "+stopShortcut)+" 取消",Foreground=Brushes.White,TextWrapping=TextWrapping.Wrap,FontSize=16}
        }};
        SourceInitialized+=(_,_)=>PositionOverlay();
        // WPF can apply monitor DPI while showing; set the final bounds again using physical pixels.
        ContentRendered+=(_,_)=>PositionOverlay();
        PreviewMouseLeftButtonDown+=(_,e)=>{e.Handled=true;_leftDown=true;Mouse.Capture(this);};
        PreviewMouseLeftButtonUp+=(_,e)=>{
            e.Handled=true;if(!_leftDown)return;_leftDown=false;Mouse.Capture(null);
            try {
                var state=_platform.Inspect(_game);
                if(state?.Identity!=_game || !state.Visible)throw new InvalidOperationException("游戏窗口已改变，请重新点选。");
                var point=_platform.ReadPhysicalCursor();
                _completion.TrySetResult(CalibrationGeometry.Capture(_area,state.Client,point.X,point.Y));
            } catch(Exception error){_completion.TrySetException(error);}
            Close();
        };
        PreviewMouseRightButtonDown+=(_,e)=>e.Handled=true;
        PreviewMouseRightButtonUp+=(_,e)=>{e.Handled=true;Cancel();};
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){e.Handled=true;Cancel();}};
        _check.Tick+=(_,_)=>{try {
            if(_platform.KeyIsDown(0x1b)){Cancel();return;}
            var state=_platform.Inspect(_game);
            if(state?.Identity!=_game || !state.Visible || state.Client!=_area){_completion.TrySetException(new InvalidOperationException("游戏窗口已移动、缩放或关闭，请重新点选。"));Close();}
        }catch(Exception error){_completion.TrySetException(error);Close();}};
        Closed+=(_,_)=>{_check.Stop();if(IsMouseCaptured)Mouse.Capture(null);_completion.TrySetResult(null);};
    }
    private void PositionOverlay()
    {
        try {WindowsGamePlatform.PositionPhysicalOverlay(new WindowInteropHelper(this).Handle,_area);}
        catch(Exception error){_completion.TrySetException(error);_ = Dispatcher.BeginInvoke(new Action(Close));}
    }
    private void Cancel(){_completion.TrySetResult(null);Close();}
    internal async Task<ButtonBinding?> PickAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var registration=token.Register(()=>Dispatcher.BeginInvoke(new Action(Cancel)));
        try {Show();_check.Start();return await _completion.Task;}
        finally {_check.Stop();if(IsVisible)Close();}
    }
}
