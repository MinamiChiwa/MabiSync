using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MabiSync.App;

internal sealed class WindowCapturePolicy
{
    public bool Apply(Window window, bool enabled, out string? error)
    {
        error=null;
        if(enabled && !OperatingSystem.IsWindowsVersionAtLeast(10,0,19041))
        {error="录屏隐藏需要 Windows 10 2004 或更高版本。";return false;}
        var handle=new WindowInteropHelper(window).Handle;
        if(handle==0)return true; // SourceInitialized applies the requested policy before the window is shown.
        if(SetWindowDisplayAffinity(handle,enabled?0x11u:0u))return true;
        error="录屏隐藏设置失败："+new Win32Exception(Marshal.GetLastWin32Error()).Message;
        return false;
    }
    public uint ReadAffinity(Window window)
    {
        if(!GetWindowDisplayAffinity(new WindowInteropHelper(window).Handle,out var affinity))throw new Win32Exception(Marshal.GetLastWin32Error());
        return affinity;
    }
    [DllImport("user32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint window,uint affinity);
    [DllImport("user32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(nint window,out uint affinity);
}
