using System.Runtime.InteropServices;

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
                await Task.Delay(100);
            }
        }
    }
}
