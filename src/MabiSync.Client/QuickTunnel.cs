using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MabiSync.Client;

public sealed record TunnelState(string Status, Uri? Endpoint, string Message);
public interface ITunnelProcess : IAsyncDisposable { int Id { get; } bool HasExited { get; } Task Exited { get; } }
public interface ITunnelLauncher { ITunnelProcess Start(string executable, int port, Action<string> line); }
public interface IQuickTunnelProbe
{
    Task<string> LocalEpochAsync(int port, CancellationToken token);
    Task<bool> VerifyAsync(Uri endpoint, string epoch, CancellationToken token);
}

/// <summary>Owns one explicitly requested temporary tunnel. No game data or room credentials are logged.</summary>
public sealed class QuickTunnel(ITunnelLauncher launcher, IQuickTunnelProbe probe, TimeSpan? startupTimeout = null, TimeSpan? healthInterval = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _run;
    private ITunnelProcess? _process;
    private Task? _healthTask;
    private int _generation;
    private volatile bool _disposed;
    private TunnelState _state = new("off", null, "公网连接未开启。");
    public TunnelState State => Volatile.Read(ref _state);
    public event Action<TunnelState>? Changed;
    public static Uri? ExtractEndpoint(string line)
    {
        var match = Regex.Match(line, @"https://([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.trycloudflare\.com)(?=$|[\s/|])", RegexOptions.IgnoreCase);
        return match.Success ? new("wss://" + match.Groups[1].Value) : null;
    }
    public async Task<TunnelState> StartAsync(string executable, int port = 3132, CancellationToken token = default)
    {
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State.Status == "ready" && _process is { HasExited: false }) return State;
            await Cleanup().ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(_disposed, this);
            _run = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_run.Token);
            limit.CancelAfter(startupTimeout ?? TimeSpan.FromSeconds(45));
            var generation = _generation;
            try
            {
                if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
                Publish(new("starting", null, "正在检查本机房间服务并建立公网连接…"));
                var epoch = await probe.LocalEpochAsync(port, limit.Token).ConfigureAwait(false);
                limit.Token.ThrowIfCancellationRequested();
                var address = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
                var process = launcher.Start(executable, port, line => { if (generation == Volatile.Read(ref _generation) && ExtractEndpoint(line) is { } endpoint) address.TrySetResult(endpoint); });
                _process = process;
                var first = await Task.WhenAny(address.Task, process.Exited).WaitAsync(limit.Token).ConfigureAwait(false);
                if (first != address.Task || process.HasExited) throw new IOException("公网连接进程提前退出。");
                var endpoint = await address.Task.ConfigureAwait(false);
                Publish(new("verifying", null, "正在核对公网房间连接，尚未生成可用邀请地址…"));
                while (!await probe.VerifyAsync(endpoint, epoch, limit.Token).ConfigureAwait(false))
                {
                    if (process.HasExited) throw new IOException("公网连接进程已退出。");
                    await Task.Delay(500, limit.Token).ConfigureAwait(false);
                }
                limit.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException("公网连接进程已退出。");
                Publish(new("ready", endpoint, "公网连接已就绪，可复制完整邀请交给另一位玩家。"));
                _ = ObserveExit(process, generation);
                _healthTask = ObserveHealth(process, endpoint, epoch, generation, _run.Token);
            }
            catch (OperationCanceledException)
            {
                var cancelled = _run.IsCancellationRequested;
                await Cleanup().ConfigureAwait(false);
                Publish(cancelled ? new("off", null, "公网连接已取消。") : new("error", null, "公网连接超时，可重试或填写其他服务地址。"));
            }
            catch (Exception error)
            {
                await Cleanup().ConfigureAwait(false);
                Publish(new("error", null, "公网连接未建立：" + error.Message));
            }
            return State;
        }
        finally { _lifecycle.Release(); }
    }
    private async Task ObserveExit(ITunnelProcess process, int generation)
    {
        try { await process.Exited.ConfigureAwait(false); if (generation == Volatile.Read(ref _generation)) Publish(new("error", null, "公网连接已断开，请重新建立；本机房间仍保留。")); }
        catch (Exception error) { Trace.WriteLine(error.GetType().Name); }
    }
    private async Task ObserveHealth(ITunnelProcess process, Uri endpoint, string epoch, int generation, CancellationToken token)
    {
        var failures=0;
        try {
            while (!token.IsCancellationRequested) {
                await Task.Delay(healthInterval??TimeSpan.FromSeconds(15),token).ConfigureAwait(false);
                using var limit=CancellationTokenSource.CreateLinkedTokenSource(token);limit.CancelAfter(TimeSpan.FromSeconds(5));
                bool reachable;
                try {reachable=await probe.VerifyAsync(endpoint,epoch,limit.Token).ConfigureAwait(false);}
                catch(Exception error) when(error is IOException or System.Net.Http.HttpRequestException or OperationCanceledException) {reachable=false;}
                if(token.IsCancellationRequested || generation!=Volatile.Read(ref _generation) || process.HasExited)return;
                failures=reachable?0:failures+1;
                if(failures>=3) {Publish(new("error",null,"公网连接连续核对失败，邀请暂不可用。请重新建立公网连接并分享新邀请；本机房间仍保留。"));return;}
            }
        } catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        catch(Exception error) { if(generation==Volatile.Read(ref _generation))Publish(new("error",null,"公网连接核对中断，请重新建立公网连接："+error.Message)); }
    }
    public async Task StopAsync()
    {
        try { Volatile.Read(ref _run)?.Cancel(); } catch (ObjectDisposedException) { }
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await Cleanup().ConfigureAwait(false); Publish(new("off", null, "公网连接已关闭。")); }
        finally { _lifecycle.Release(); }
    }
    private async Task Cleanup()
    {
        Interlocked.Increment(ref _generation);
        var process = _process; _process = null;
        var run = _run; _run = null; run?.Cancel();
        var health=_healthTask;_healthTask=null;
        try { if(health is not null)await health.ConfigureAwait(false); if (process is not null) await process.DisposeAsync().ConfigureAwait(false); }
        finally { run?.Dispose(); }
    }
    private void Publish(TunnelState state)
    {
        Volatile.Write(ref _state, state);
        if (Changed is { } handlers) foreach (Action<TunnelState> handler in handlers.GetInvocationList())
            try { handler(state); } catch (Exception error) { Trace.WriteLine(error.GetType().Name); }
    }
    public async ValueTask DisposeAsync() { _disposed = true; await StopAsync().ConfigureAwait(false); }
}

public sealed class CloudflaredLauncher : ITunnelLauncher
{
    public ITunnelProcess Start(string executable, int port, Action<string> line)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "tunnel", "--url", $"http://127.0.0.1:{port}", "--no-autoupdate", "--protocol", "http2" }) info.ArgumentList.Add(arg);
        var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => { if (e.Data is { } value) line(value); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { } value) line(value); };
        try { if (!process.Start()) throw new IOException("无法启动公网连接组件。"); process.BeginOutputReadLine(); process.BeginErrorReadLine(); return new OwnedProcess(process); }
        catch { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } process.Dispose(); throw; }
    }
    private sealed class OwnedProcess(Process process) : ITunnelProcess
    {
        public int Id => process.Id;
        public bool HasExited => process.HasExited;
        public Task Exited { get; } = process.WaitForExitAsync();
        public async ValueTask DisposeAsync()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); await Exited.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            finally { process.Dispose(); }
        }
    }
}
