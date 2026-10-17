using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MabiSync.Core;

namespace MabiSync.Windows;

public sealed record CaptureObservation(GameObservation Game, long Packets, bool Running, string Status);

/// <summary>Main-window-owned passive observer. Calibration windows only view/control it.</summary>
public sealed class GameObservationCapture : IAsyncDisposable
{
    private CancellationTokenSource? _stop;
    private Task? _worker;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CaptureObservation _current = Empty("尚未开启抓包识别。");
    private volatile bool _disposed;
    private GameWindowId? _game;
    private CaptureDevice? _device;
    public GameWindowId? Game=>Volatile.Read(ref _game);
    public CaptureDevice? Device=>Volatile.Read(ref _device);
    public CaptureObservation Current => Volatile.Read(ref _current);
    public event Action<CaptureObservation>? Changed;
    internal void Publish(CaptureObservation value) { Volatile.Write(ref _current, value); Changed?.Invoke(value); }
    private static CaptureObservation Empty(string message) => new(new(Guid.NewGuid().ToString("N"), 1, null, null, message), 0, false, message);
    public async Task StartAsync(GameWindowId game, CaptureDevice device)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is { IsCompleted: false }) throw new InvalidOperationException("正在抓包，请先停止识别再切换通道。");
            _stop?.Dispose(); _stop = new(); var token = _stop.Token;
            Volatile.Write(ref _game,game);Volatile.Write(ref _device,device);
            Publish(Empty("正在核对客户端版本与抓包通道。") with { Running = true });
            _worker = Task.Run(async () =>
            {
                try { await CaptureChannels.ObserveAsync(game, device, Publish, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception error) { Publish(Empty(Explain(error))); }
                finally { if (Current.Running) Publish(Empty("抓包识别已停止，下次开启时会重新识别昵称和加入顺序。")); }
            }, CancellationToken.None);
        }
        finally { _lifecycle.Release(); }
    }
    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { _stop?.Cancel(); if (_worker is not null) await _worker.ConfigureAwait(false); Publish(Empty("抓包识别已停止，昵称和加入顺序需要重新识别。")); }
        finally { _lifecycle.Release(); }
    }
    public async ValueTask DisposeAsync()
    { _disposed = true; await StopAsync().ConfigureAwait(false); _stop?.Dispose(); }
    private static string Explain(Exception error) => error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException
        ? "Npcap 不可用，暂时无法自动识别昵称和加入顺序。同步开始和手动停止仍可使用。"
        : "抓包识别已停止：" + error.Message;
}

public static partial class CaptureChannels
{
    private sealed class ReceiveChannel(nint handle, int link, CaptureConnection connection, double opened) : IDisposable
    {
        public nint Handle { get; } = handle;
        public int Link { get; } = link;
        public CaptureConnection Connection { get; } = connection;
        public TwGameObserver Observer { get; } = new();
        public GameTcpStream Stream { get; set; } = null!;
        public double InitializerAt { get; set; } = double.NegativeInfinity;
        public double Opened { get; } = opened;
        public uint? LastSyn { get; set; }
        public uint Drops { get; set; }
        public void Dispose() => pcap_close(Handle);
    }
    internal static async Task ObserveAsync(GameWindowId game, CaptureDevice device, Action<CaptureObservation> publish, CancellationToken token)
    {
        if (!WindowsGamePlatform.IsProcessRunning(game)) throw new IOException("所选游戏进程已变化，请重新选择。");
        await VerifyClientAsync(game.ImagePath, token).ConfigureAwait(false);
        var channels = new Dictionary<CaptureConnection, ReceiveChannel>();
        var timer = Stopwatch.StartNew(); double lastPoll = -1000, lastReport = -1000; long packets = 0;
        var empty = new TwGameObserver(); string? lastError = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = timer.Elapsed.TotalMilliseconds;
                if (now - lastPoll >= 500)
                {
                    if (!WindowsGamePlatform.IsProcessRunning(game)) throw new IOException("所选游戏进程已退出或重新启动。");
                    var current = Connections(game.ProcessId).Where(c => device.Addresses.Contains(c.LocalAddress)).Take(32).ToHashSet();
                    foreach (var old in channels.Keys.Where(c => !current.Contains(c)).ToArray()) { channels[old].Dispose(); channels.Remove(old); }
                    foreach (var connection in current.Where(c => !channels.ContainsKey(c)))
                    {
                        try { channels[connection] = OpenReceive(device, connection, now); lastError = null; }
                        catch (IOException error) { lastError = error.Message; }
                    }
                    foreach (var channel in channels.Values)
                    {
                        if (pcap_stats(channel.Handle, out var stats) == 0)
                        {
                            var drops = unchecked(stats.Dropped + stats.InterfaceDropped);
                            if (drops != channel.Drops) { channel.Stream.Restart("抓包数据不完整，需要重新识别昵称和加入顺序。"); channel.Drops = drops; }
                        }
                    }
                    lastPoll = now;
                }
                foreach (var channel in channels.Values)
                {
                    for (var i = 0; i < 200; i++)
                    {
                        var result = pcap_next_ex(channel.Handle, out var header, out var pointer);
                        if (result == 0) break;
                        if (result != 1) throw new IOException("Npcap 读取中断，请重新校对通道。");
                        // Windows Npcap timeval uses two int32 fields; caplen follows at +8.
                        var length = Marshal.ReadInt32(header + 8); var wireLength = Marshal.ReadInt32(header + 12);
                        if (length < 0 || length > 65536 || length > wireLength) { channel.Stream.Restart("抓到的数据长度异常，需要重新识别。"); continue; }
                        var raw = new byte[length]; Marshal.Copy(pointer, raw, 0, length); packets++;
                        var packet = GamePacket.Read(channel.Link, raw);
                        if (packet is null || packet.SourceAddress != channel.Connection.RemoteAddress || packet.SourcePort != channel.Connection.RemotePort || packet.DestinationAddress != channel.Connection.LocalAddress || packet.DestinationPort != channel.Connection.LocalPort) continue;
                        if ((packet.Flags & 2) != 0 && channel.LastSyn != packet.Sequence)
                        { channel.LastSyn = packet.Sequence; channel.Stream.Restart("游戏连接已更新，正在重新识别角色昵称。"); }
                        var mono = Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency);
                        var capturedUtc = Marshal.ReadInt32(header) * 1000d + Marshal.ReadInt32(header + 4) / 1000d;
                        var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - capturedUtc;
                        // Use Npcap's packet timestamp, not a later UI/parser wake.
                        // An invalid mapping loses only this frame's timing estimate.
                        var receipt = age is >= -100 and <= 10000 ? mono - age : double.NaN;
                        channel.Stream.Feed(unchecked(packet.Sequence + (uint)((packet.Flags & 2) != 0 ? 1 : 0)), packet.Payload, mono, receipt);
                        if ((packet.Flags & 5) != 0) channel.Stream.Restart("游戏连接已结束，等待新连接。");
                    }
                    channel.Stream.CheckGap(Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency));
                }
                if (now - lastReport >= 500)
                {
                    // Never fall back to an older channel during a new initializer's pending phase.
                    var primary = channels.Values.Where(c => !double.IsNegativeInfinity(c.InitializerAt)).MaxBy(c => c.InitializerAt);
                    var observation = primary?.Observer.Current ?? empty.Current;
                    var status = channels.Count == 0 ? lastError ?? "等待所选网卡上的游戏连接。" : observation.Detail;
                    publish(new(observation, packets, true, status)); lastReport = now;
                }
                await Task.Delay(10, token).ConfigureAwait(false);
            }
        }
        finally { foreach (var channel in channels.Values) channel.Dispose(); }
    }
    private static ReceiveChannel OpenReceive(CaptureDevice device, CaptureConnection connection, double now)
    {
        var error = new StringBuilder(256); var handle = pcap_open_live(device.Name, 65536, 0, 100, error);
        if (handle == 0) throw new IOException("无法打开所选网卡：" + error);
        try
        {
            // Immutable filter per connection, downlink only. Never open a broad machine filter.
            var expression = $"tcp and src host {connection.RemoteAddress} and src port {connection.RemotePort} and dst host {connection.LocalAddress} and dst port {connection.LocalPort}";
            if (pcap_compile(handle, out var filter, expression, 1, uint.MaxValue) != 0) throw new IOException("无法建立连接过滤器。");
            try { if (pcap_setfilter(handle, ref filter) != 0) throw new IOException("无法应用连接过滤器。"); }
            finally { pcap_freecode(ref filter); }
            if (pcap_setnonblock(handle, 1, error) != 0) throw new IOException("Npcap 无法启动抓包，请检查安装或尝试其他网卡。");
            var link = pcap_datalink(handle);
            if (link is not (0 or 1 or 12 or 101)) throw new IOException("暂不支持此网卡，请选择其他网卡重试。");
            var channel = new ReceiveChannel(handle, link, connection, now);
            channel.Stream = new((code, body) =>
            {
                if (code == 20520) channel.InitializerAt = Stopwatch.GetTimestamp();
                channel.Observer.Accept(code, body, channel.Stream.FrameReceivedLocalMs);
            }, reason => channel.Observer.Reset(reason));
            return channel;
        }
        catch { pcap_close(handle); throw; }
    }
    private static async Task VerifyClientAsync(string imagePath, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(imagePath)!;
        var files = new[] { (Path.Combine(directory, "GameAssembly.dll"), TwGameProtocol.AssemblySha256),
            (Path.Combine(directory, Path.GetFileNameWithoutExtension(imagePath) + "_Data", "il2cpp_data", "Metadata", "global-metadata.dat"), TwGameProtocol.MetadataSha256) };
        foreach (var (path, expected) in files)
        {
            var before = new FileInfo(path); var length = before.Length; var modified = before.LastWriteTimeUtc;
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
            var after = new FileInfo(path);
            if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase) || after.Length != length || after.LastWriteTimeUtc != modified)
                throw new IOException("暂不支持此游戏版本的抓包识别，同步开始和手动停止仍可使用。");
        }
    }
    // Reserve Windows WinPcap/Npcap extension fields too; never pass a smaller native buffer.
    [StructLayout(LayoutKind.Sequential)] private struct PcapStats { public uint Received, Dropped, InterfaceDropped, Captured, Sent, NetworkDropped; }
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_datalink(nint handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int pcap_stats(nint handle, out PcapStats stats);
}
