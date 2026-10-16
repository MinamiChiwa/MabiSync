using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using MabiSync.Core;

namespace MabiSync.Windows;

public sealed record CaptureDevice(string Name, string Description, string[] Addresses)
{
    public string Label => $"{Description} · {string.Join(", ", Addresses)}";
}
public sealed record CaptureConnection(string LocalAddress, ushort LocalPort, string RemoteAddress, ushort RemotePort)
{
    public string Label => $"{LocalAddress}:{LocalPort} ↔ {RemoteAddress}:{RemotePort}";
    public string Filter => $"tcp and ((src host {LocalAddress} and src port {LocalPort} and dst host {RemoteAddress} and dst port {RemotePort}) or (src host {RemoteAddress} and src port {RemotePort} and dst host {LocalAddress} and dst port {LocalPort}))";
}
public sealed record ChannelCheck(long Packets, double ElapsedSeconds, string Status);

/// <summary>Optional, read-only Npcap diagnostics. Never saves raw packets or transmits traffic.</summary>
public static partial class CaptureChannels
{
    private const string Library = "MabiSync.OptionalNpcap";
    static CaptureChannels()
    {
        NativeLibrary.SetDllImportResolver(typeof(CaptureChannels).Assembly, (name, _, _) => name == Library
            ? LoadLibraryEx(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Npcap", "wpcap.dll"), 0, 0x100 | 0x800) : 0);
    }
    public static IReadOnlyList<CaptureDevice> Devices()
    {
        var error = new StringBuilder(256);
        if (pcap_findalldevs(out var head, error) != 0) throw new IOException(error.ToString());
        var devices = new List<CaptureDevice>();
        try
        {
            for (var next = head; next != 0;)
            {
                var item = Marshal.PtrToStructure<Device>(next); var addresses = new List<string>();
                for (var addr = item.Addresses; addr != 0;)
                {
                    var row = Marshal.PtrToStructure<Address>(addr);
                    if (row.Sockaddr != 0 && Marshal.ReadInt16(row.Sockaddr) == 2)
                    { var bytes = new byte[4]; Marshal.Copy(row.Sockaddr + 4, bytes, 0, 4); addresses.Add(new IPAddress(bytes).ToString()); }
                    addr = row.Next;
                }
                devices.Add(new(Marshal.PtrToStringUTF8(item.Name) ?? "", Marshal.PtrToStringUTF8(item.Description) ?? "网卡", addresses.ToArray()));
                next = item.Next;
            }
        }
        finally { pcap_freealldevs(head); }
        return devices;
    }
    public static IReadOnlyList<CaptureConnection> Connections(int processId)
    {
        uint size = 0; var result = GetExtendedTcpTable(0, ref size, false, 2, 5, 0);
        if (result is not (0 or 122)) throw new IOException("无法读取游戏连接列表：" + result);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var memory = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                result = GetExtendedTcpTable(memory, ref size, false, 2, 5, 0);
                if (result == 122) continue;
                if (result != 0) throw new IOException("无法读取游戏连接列表：" + result);
                var count = Marshal.ReadInt32(memory);
                if (count < 0 || 4L + count * 24L > size) throw new IOException("游戏连接列表长度异常。");
                var rows = new List<CaptureConnection>();
                for (var i = 0; i < count; i++)
                {
                    var at = memory + 4 + i * 24;
                    if (Marshal.ReadInt32(at + 20) != processId || Marshal.ReadInt32(at) != 5) continue;
                    var local = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(at + 4))).ToString();
                    var remote = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(at + 12))).ToString();
                    var localPort = (ushort)IPAddress.NetworkToHostOrder(Marshal.ReadInt16(at + 8));
                    var remotePort = (ushort)IPAddress.NetworkToHostOrder(Marshal.ReadInt16(at + 16));
                    if (localPort != 0 && remotePort != 0) rows.Add(new(local, localPort, remote, remotePort));
                }
                return rows;
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        throw new IOException("连接正在变化，请刷新后重试。");
    }
    public static Task CheckAsync(GameWindowId game, CaptureDevice device, CaptureConnection connection, IProgress<ChannelCheck> progress, CancellationToken token) => Task.Run(() =>
    {
        if (!WindowsGamePlatform.IsProcessRunning(game) || !Connections(game.ProcessId).Contains(connection)) throw new IOException("所选游戏或连接已变化，请重新选择。");
        if (!device.Addresses.Contains(connection.LocalAddress)) throw new IOException("所选网卡不包含这条连接的本机地址。");
        var error = new StringBuilder(256);
        var handle = pcap_open_live(device.Name, 65536, 0, 100, error);
        if (handle == 0) throw new IOException("Npcap 无法打开网卡：" + error);
        try
        {
            if (pcap_compile(handle, out var filter, connection.Filter, 1, uint.MaxValue) != 0) throw new IOException("无法建立游戏连接过滤器。");
            try { if (pcap_setfilter(handle, ref filter) != 0) throw new IOException("无法应用游戏连接过滤器。"); }
            finally { pcap_freecode(ref filter); }
            if (pcap_setnonblock(handle, 1, error) != 0) throw new IOException("Npcap 无法使用非阻塞模式。");
            var timer = System.Diagnostics.Stopwatch.StartNew(); long count = 0; double last = -1;
            while (!token.IsCancellationRequested && timer.Elapsed.TotalSeconds < 30)
            {
                for (var i = 0; i < 100; i++)
                {
                    var result = pcap_next_ex(handle, out _, out _);
                    if (result == 0) break;
                    if (result != 1) throw new IOException("Npcap 读取中断。");
                    count++;
                }
                if (timer.Elapsed.TotalSeconds - last >= .5)
                {
                    if (!Connections(game.ProcessId).Contains(connection)) { progress.Report(new(count, timer.Elapsed.TotalSeconds, "游戏连接已变化，已停止校对。")); return; }
                    progress.Report(new(count, timer.Elapsed.TotalSeconds, count == 0 ? "等待所选游戏连接的流量。" : "已收到所选游戏连接流量；尚不代表已识别本机角色。"));
                    last = timer.Elapsed.TotalSeconds;
                }
                token.WaitHandle.WaitOne(15);
            }
            progress.Report(new(count, timer.Elapsed.TotalSeconds, token.IsCancellationRequested ? "校对已停止。" : "30 秒校对完成。"));
        }
        finally { pcap_close(handle); }
    }, token);
    [StructLayout(LayoutKind.Sequential)] private struct Device { public nint Next, Name, Description, Addresses; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Address { public nint Next, Sockaddr, Netmask, Broadcast, Destination; }
    [StructLayout(LayoutKind.Sequential)] private struct Bpf { public uint Length; public nint Instructions; }
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadLibraryEx(string path, nint file, uint flags);
    [DllImport("iphlpapi")] private static extern uint GetExtendedTcpTable(nint data, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint table, uint reserved);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_findalldevs(out nint head, StringBuilder error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void pcap_freealldevs(nint head);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern nint pcap_open_live([MarshalAs(UnmanagedType.LPUTF8Str)] string name, int snap, int promisc, int timeout, StringBuilder error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_compile(nint handle, out Bpf filter, string expression, int optimize, uint netmask);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_setfilter(nint handle, ref Bpf filter);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void pcap_freecode(ref Bpf filter);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_setnonblock(nint handle, int value, StringBuilder error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_next_ex(nint handle, out nint header, out nint packet);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void pcap_close(nint handle);
}
