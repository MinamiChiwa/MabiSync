using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Text;

namespace MabiSync.Windows;

public static class ClipboardCopy
{
    // Keep retries on the caller's UI context; WPF clipboard access requires STA.
    public static async Task WriteAsync(string text, Action<string> write)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { write(text); return; }
            catch (ExternalException error) when (error.ErrorCode == unchecked((int)0x800401D0))
            {
                if (attempt == 7) throw new InvalidOperationException("剪贴板正被其他程序占用，请稍后再点“复制完整邀请”。", error);
                await Task.Delay(100+attempt*40);
            }
        }
    }
    /// <summary>Write persistent Unicode text without OLE clipboard flushing/re-entrancy.</summary>
    public static void WriteText(nint owner,string text)
    {
        if(owner==0)throw new ArgumentException("A clipboard owner window is required",nameof(owner));
        if(!OpenClipboard(owner))throw new ExternalException("Clipboard is busy",unchecked((int)0x800401D0));
        nint memory=0;
        try {
            var bytes=Encoding.Unicode.GetBytes(text+'\0');
            memory=GlobalAlloc(2,(nuint)bytes.Length);
            if(memory==0)throw new Win32Exception(Marshal.GetLastWin32Error());
            var pointer=GlobalLock(memory);
            if(pointer==0)throw new Win32Exception(Marshal.GetLastWin32Error());
            try{Marshal.Copy(bytes,0,pointer,bytes.Length);}finally{GlobalUnlock(memory);}
            if(!EmptyClipboard() || SetClipboardData(13,memory)==0)throw new Win32Exception(Marshal.GetLastWin32Error());
            memory=0; // Clipboard owns the movable allocation after SetClipboardData succeeds.
        } finally {if(memory!=0)GlobalFree(memory);CloseClipboard();}
    }
    [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CloseClipboard();
    [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool EmptyClipboard();
    [DllImport("user32.dll",SetLastError=true)]private static extern nint SetClipboardData(uint format,nint memory);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern nint GlobalAlloc(uint flags,nuint bytes);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")]private static extern nint GlobalFree(nint memory);
}
