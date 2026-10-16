using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MabiSync.Core;

/// <summary>Windows high resolution one-shot wait, interrupted by cancellation/new work.</summary>
internal sealed class DeadlineWaiter : IDisposable
{
    private readonly EventWaitHandle? _timer;
    public DeadlineWaiter()
    {
        if(!OperatingSystem.IsWindows())return;
        var handle=CreateWaitableTimerEx(0,null,2,0x1F0003);
        if(handle==0)return; // Older Windows: fall back to a short early wake.
        _timer=new EventWaitHandle(false,EventResetMode.AutoReset);
        _timer.SafeWaitHandle=new SafeWaitHandle(handle,true);
    }
    public void Wait(AutoResetEvent wake,double milliseconds)
    {
        if(_timer is not null && milliseconds>1) {
            long due=-Math.Max(1,(long)((Math.Min(milliseconds,250)-.5)*10000));
            if(SetWaitableTimer(_timer.SafeWaitHandle,ref due,0,0,0,false)) {
                WaitHandle.WaitAny([wake,_timer],300);return;
            }
        }
        if(milliseconds>16)wake.WaitOne((int)Math.Clamp(Math.Floor(milliseconds-15),1,250));
        else Thread.Yield();
    }
    public void Dispose()=>_timer?.Dispose();
    [DllImport("kernel32.dll",EntryPoint="CreateWaitableTimerExW",CharSet=CharSet.Unicode)]
    private static extern nint CreateWaitableTimerEx(nint attributes,string? name,uint flags,uint access);
    [DllImport("kernel32.dll")]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle handle,ref long due,int period,nint completion,nint argument,[MarshalAs(UnmanagedType.Bool)]bool resume);
}
