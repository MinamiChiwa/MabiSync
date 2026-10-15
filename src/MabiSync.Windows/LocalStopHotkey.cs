using System.Runtime.InteropServices;

namespace MabiSync.Windows;

public sealed class LocalStopHotkey : IDisposable
{
    public const int Id = 0x4d53;
    private readonly Thread _thread;
    private uint _threadId;
    public bool Registered { get; private set; }
    public LocalStopHotkey(Action stop)
    {
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            PeekMessage(out _, 0, 0, 0, 0); // Create this thread's message queue before Dispose can post WM_QUIT.
            Registered = RegisterHotKey(0, Id, 0x4000, 0x78); ready.Set();
            if (!Registered) return;
            try { while (GetMessage(out var message, 0, 0, 0) > 0) if (message.Id == 0x0312 && message.WParam == Id) try { stop(); } catch (Exception) { /* callback owns its diagnostics */ } }
            finally { UnregisterHotKey(0, Id); }
        }) { Name = "MabiSync local stop hotkey", IsBackground = true };
        _thread.Start(); ready.Wait();
    }
    public void Dispose() { if (_thread.IsAlive) { PostThreadMessage(_threadId, 0x0012, 0, 0); _thread.Join(1000); } }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint threadId, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(nint window, int id);
}
