using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace MabiSync.App;

internal sealed class LocalService : IDisposable
{
    private readonly Func<Task<bool>> _probe;
    internal LocalService(Func<Task<bool>>? probe = null) { _probe = probe ?? Probe; }
    private Process? _owned;
    private bool _lan;
    private volatile bool _disposed;
    public string Status { get; private set; } = "";
    public async Task StartAsync(bool lan)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_owned is not null)
        {
            if (_owned.HasExited) { _owned.Dispose(); _owned = null; }
            else
            {
                if (_lan != lan) throw new IOException("本机服务已启动。如需改变局域网设置，请先关闭并重新打开工具，避免中断现有房间。");
                if (await ProbeOwnedAsync()) return;
                throw new IOException("本机服务已启动但尚未响应，请稍后重试。");
            }
        }
        if (await ProbeOwnedAsync()) { Status = "已连接现有 MabiSync 服务；局域网是否可用取决于该服务的监听设置。"; return; }
        var root = Path.Combine(AppContext.BaseDirectory, "Service");
        var script = Path.Combine(root, "server", "index.js");
        if (!File.Exists(script)) throw new IOException("找不到随程序提供的房间服务文件。");
        var bundled = Path.Combine(AppContext.BaseDirectory, "runtime", "node.exe");
        var info = new ProcessStartInfo(File.Exists(bundled) ? bundled : "node") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(script); info.Environment["MABISYNC_PORT"] = "3132";
        info.Environment["MABISYNC_HOST"] = lan ? "0.0.0.0" : "127.0.0.1";
        _owned = Process.Start(info) ?? throw new IOException("无法启动房间服务。请确认完整解压发布包；源码运行需要 Node.js 20 或更高版本。");
        _lan = lan;
        Status = lan ? "局域网服务已启动。其他玩家填写本机局域网地址与端口 3132；请按系统提示允许所需连接。" : "本机服务已启动。";
        for (var attempt = 0; attempt < 30; attempt++)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_owned.HasExited) throw new IOException("房间服务已退出。请检查 3132 端口是否被其他程序占用。");
            if (await ProbeOwnedAsync()) return;
            await Task.Delay(100);
        }
        throw new IOException("房间服务尚未就绪，可以稍后重新连接。");
    }
    private async Task<bool> ProbeOwnedAsync()
    {
        var result = await _probe();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return result;
    }
    private static async Task<bool> Probe()
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(1) };
        try
        {
            using var response = await http.GetAsync("http://127.0.0.1:3132/health");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var value = doc.RootElement;
            if (response.IsSuccessStatusCode && value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("product", out var product) && product.ValueKind == JsonValueKind.String && product.GetString() == "MabiSync" &&
                value.TryGetProperty("protocolVersion", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number == 1)
            {
                if (!value.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Object ||
                    !features.TryGetProperty("gameObservation", out var observation) || observation.ValueKind != JsonValueKind.Number || !observation.TryGetInt32(out var supported) || supported != 1 ||
                    !features.TryGetProperty("firstCycleStop", out var cycle) || cycle.ValueKind != JsonValueKind.Number || !cycle.TryGetInt32(out var cycleSupported) || cycleSupported != 1 ||
                    !features.TryGetProperty("rosterEditing",out var editing) || editing.ValueKind!=JsonValueKind.Number || !editing.TryGetInt32(out var editingSupported) || editingSupported!=1 ||
                    !features.TryGetProperty("roomClosure",out var closure) || closure.ValueKind!=JsonValueKind.Number || !closure.TryGetInt32(out var closureSupported) || closureSupported!=1)
                    throw new IOException("3132 端口仍在运行旧版 MabiSync 服务，请先关闭旧工具后重试。");
                return true;
            }
            throw new IOException("3132 端口运行的服务与 MabiSync 协议不匹配。");
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
        catch (JsonException) { throw new IOException("3132 端口被其他服务占用。"); }
    }
    public void Dispose()
    {
        _disposed = true;
        if (_owned is null) return;
        try { if (!_owned.HasExited) _owned.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        _owned.Dispose(); _owned = null;
    }
}
