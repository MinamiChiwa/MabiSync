namespace MabiSync.Core;

/// <summary>All values are physical pixels. No conversion through WPF device-independent units.</summary>
public static class CalibrationGeometry
{
    public static ButtonBinding Capture(ClientArea overlayArea, ClientArea currentArea, int screenX, int screenY)
    {
        if (overlayArea != currentArea) throw new InvalidOperationException("游戏窗口已移动或缩放，请重新点选按钮位置。");
        var x=(long)screenX-currentArea.Left;var y=(long)screenY-currentArea.Top;
        if (currentArea.Width<=0 || currentArea.Height<=0 || currentArea.Dpi==0 || x<0 || y<0 || x>=currentArea.Width || y>=currentArea.Height)
            throw new InvalidOperationException("请在游戏画面内点选按钮位置。");
        return new((int)x,(int)y,currentArea.Width,currentArea.Height,currentArea.Dpi);
    }
}
