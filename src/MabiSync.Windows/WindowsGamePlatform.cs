using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using MabiSync.Core;
using Microsoft.Win32.SafeHandles;

namespace MabiSync.Windows;

/// <summary>OS window metadata and ordinary SendInput only. No game memory handles or injection.</summary>
public sealed class WindowsGamePlatform : IGameInputPlatform
{
    public Task ActivateAsync(GameWindowId window,CancellationToken token=default)=>WindowActivation.ActivateAsync(window,Inspect,
        game=>Native.ShowWindowAsync(game.Handle,9),game=>Native.SetForegroundWindow(game.Handle),token);
    public (int X,int Y) ReadPhysicalCursor()
    {
        using var dpi=new PhysicalDpiScope();
        if(!Native.GetCursorPos(out var point))throw new Win32Exception(Marshal.GetLastWin32Error(),"无法读取鼠标位置。");
        return(point.X,point.Y);
    }
    public static void PositionPhysicalOverlay(nint overlay,ClientArea area)
    {
        using var dpi=new PhysicalDpiScope();
        if(!Native.SetWindowPos(overlay,-1,area.Left,area.Top,area.Width,area.Height,0x0010|0x0040))throw new Win32Exception(Marshal.GetLastWin32Error(),"无法定位按钮点选层。");
    }
    public ButtonBinding ReadCursorCalibration(GameWindowId window)
    {
        using var dpi = new PhysicalDpiScope();
        var state = Inspect(window) ?? throw new InvalidOperationException("游戏窗口已失效。");
        if (!Native.GetCursorPos(out var point) || !PointBelongsToWindow(window, point.X, point.Y)) throw new InvalidOperationException("请把鼠标停在目标游戏按钮上。");
        var x = point.X - state.Client.Left; var y = point.Y - state.Client.Top;
        if (x < 0 || y < 0 || x >= state.Client.Width || y >= state.Client.Height) throw new InvalidOperationException("鼠标不在游戏客户区。");
        return new(x, y, state.Client.Width, state.Client.Height, state.Client.Dpi);
    }
    public IReadOnlyList<GameWindowState> ListGameWindows()
    {
        List<GameWindowState> result = [];
        Native.EnumWindows((window, _) =>
        {
            var id = ReadIdentity(window);
            if (id is not null && string.Equals(Path.GetFileName(id.ImagePath), "MabinogiMobile.exe", StringComparison.OrdinalIgnoreCase) &&
                Native.IsWindowVisible(window) && Inspect(id) is { } state && state.Client.Width > 0 && state.Client.Height > 0) result.Add(state);
            return true;
        }, 0);
        return result;
    }
    public GameWindowState? Inspect(GameWindowId window)
    {
        using var dpi = new PhysicalDpiScope();
        var id = ReadIdentity(window.Handle);
        if (id is null || !Native.GetClientRect(window.Handle, out var rect)) return null;
        var point = new Native.Point(); if (!Native.ClientToScreen(window.Handle, ref point)) return null;
        using var process = Native.OpenProcess(0x1000, false, (uint)id.ProcessId); // PROCESS_QUERY_LIMITED_INFORMATION, never VM_READ/VM_WRITE
        var targetIntegrity = Integrity(process.DangerousGetHandle()); var ownIntegrity = Integrity(Native.GetCurrentProcess());
        return new(id, new(point.X, point.Y, rect.Right - rect.Left, rect.Bottom - rect.Top, Native.GetDpiForWindow(window.Handle)),
            Native.IsWindowVisible(window.Handle) && !Native.IsIconic(window.Handle), Native.GetForegroundWindow() == window.Handle,
            targetIntegrity is not null && ownIntegrity is not null && ownIntegrity >= targetIntegrity);
    }
    public bool KeyIsDown(ushort key) => (Native.GetAsyncKeyState(key) & 0x8000) != 0;
    public bool LeftButtonIsDown() => KeyIsDown(1);
    public bool PointBelongsToWindow(GameWindowId window, int screenX, int screenY)
    {
        using var dpi = new PhysicalDpiScope();
        var top = Native.WindowFromPoint(new(screenX, screenY));
        return top != 0 && Native.GetAncestor(top, 2) == window.Handle;
    }
    public void Key(ushort key, bool down) => Send(new() { Type = 1, Data = new() { Keyboard = new() { VirtualKey = key, Flags = KeyboardInputFlags.For(key, down) } } });
    public void LeftButton(bool down) => Send(new() { Type = 0, Data = new() { Mouse = new() { Flags = down ? 2u : 4u } } });
    public void MovePointer(int screenX, int screenY)
    {
        using var dpi = new PhysicalDpiScope();
        var left = Native.GetSystemMetrics(76); var top = Native.GetSystemMetrics(77);
        var width = Native.GetSystemMetrics(78); var height = Native.GetSystemMetrics(79);
        var (x, y) = DesktopCoordinates.Normalize(screenX, screenY, left, top, width, height);
        Send(new() { Type = 0, Data = new() { Mouse = new() { X = x, Y = y, Flags = 0x0001 | 0x8000 | 0x4000 } } });
    }
    private static void Send(Native.Input input)
    {
        if (Native.SendInput(1, [input], Marshal.SizeOf<Native.Input>()) != 1) throw new Win32Exception(Marshal.GetLastWin32Error(), "系统未接受输入，请检查游戏焦点及输入权限。");
    }
    private static GameWindowId? ReadIdentity(nint window)
    {
        if (!Native.IsWindow(window)) return null;
        Native.GetWindowThreadProcessId(window, out var pid); if (pid == 0) return null;
        using var process = Native.OpenProcess(0x1000, false, pid);
        if (process.IsInvalid) return null;
        var path = new StringBuilder(32768); var size = path.Capacity;
        if (!Native.QueryFullProcessImageName(process, 0, path, ref size) || !Native.GetProcessTimes(process, out var created, out _, out _, out _)) return null;
        return new(window, (int)pid, DateTime.FromFileTimeUtc(created).Ticks, path.ToString());
    }
    private static int? Integrity(nint process)
    {
        if (!Native.OpenProcessToken(process, 8, out var token)) return null;
        using (token)
        {
            Native.GetTokenInformation(token, 25, 0, 0, out var length);
            if (length <= 0 || length > 65536) return null;
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!Native.GetTokenInformation(token, 25, buffer, length, out _)) return null;
                var sid = Marshal.ReadIntPtr(buffer);
                var countPtr = Native.GetSidSubAuthorityCount(sid); if (countPtr == 0) return null;
                var count = Marshal.ReadByte(countPtr); if (count == 0) return null;
                var rid = Native.GetSidSubAuthority(sid, (uint)(count - 1));
                return rid == 0 ? null : Marshal.ReadInt32(rid);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
    private sealed class PhysicalDpiScope : IDisposable
    {
        private readonly nint _previous = Native.SetThreadDpiAwarenessContext(-4);
        public void Dispose() { if (_previous != 0) Native.SetThreadDpiAwarenessContext(_previous); }
    }
    private static class Native
    {
        internal delegate bool EnumWindow(nint hwnd, nint parameter);
        [StructLayout(LayoutKind.Sequential)] internal struct Point(int x, int y) { public int X = x, Y = y; }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputUnion Data; }
        [StructLayout(LayoutKind.Explicit)] internal struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
        [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nuint Extra; }
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindow callback, nint parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindowAsync(nint hwnd,int command);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint hwnd);
        [DllImport("user32.dll",SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int width,int height,uint flags);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
        [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClientRect(nint hwnd, out Rect rect);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ClientToScreen(nint hwnd, ref Point point);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
        [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint context);
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] input, int size);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
        [DllImport("kernel32.dll")] internal static extern nint GetCurrentProcess();
        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, nint buffer, int length, out int required);
        [DllImport("advapi32.dll")] internal static extern nint GetSidSubAuthorityCount(nint sid);
        [DllImport("advapi32.dll")] internal static extern nint GetSidSubAuthority(nint sid, uint index);
    }
}
