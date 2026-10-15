using System.Net.WebSockets;
using System.Text.Json;

namespace MabiSync.Client;

public sealed class QuickTunnelProbe : IQuickTunnelProbe
{
    public async Task<string> LocalEpochAsync(int port, CancellationToken token)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
        using var response = await http.GetAsync($"http://127.0.0.1:{port}/health", token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        return Identity(doc.RootElement) ?? throw new IOException("本机端口上的服务不是兼容的 MabiSync 房间。");
    }
    private static string? Identity(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("product", out var p) && p.GetString() == "MabiSync" &&
            root.TryGetProperty("protocolVersion", out var v) && v.TryGetInt32(out var version) && version == 1 &&
            root.TryGetProperty("serverEpoch", out var e) && Guid.TryParse(e.GetString(), out var epoch)) return epoch.ToString();
        return null;
    }
    public async Task<bool> VerifyAsync(Uri endpoint, string epoch, CancellationToken token)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token); attempt.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var http = new HttpClient();
            var health = new UriBuilder(endpoint) { Scheme = endpoint.Scheme == "wss" ? "https" : "http", Path = "/health" }.Uri;
            using var response = await http.GetAsync(health, attempt.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(attempt.Token).ConfigureAwait(false));
            if (Identity(doc.RootElement) != epoch) return false;
            using var ws = new ClientWebSocket(); await ws.ConnectAsync(endpoint, attempt.Token).ConfigureAwait(false);
            using var hello = await Receive(ws, attempt.Token).ConfigureAwait(false);
            if (hello.RootElement.GetProperty("type").GetString() != "hello" || Identity(hello.RootElement) != epoch) return false;
            await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "hello", product = "MabiSync", protocolVersion = 1 }).AsMemory(), WebSocketMessageType.Text, true, attempt.Token).ConfigureAwait(false);
            using var accepted = await Receive(ws, attempt.Token).ConfigureAwait(false);
            return accepted.RootElement.GetProperty("type").GetString() == "hello-accepted" && accepted.RootElement.GetProperty("serverEpoch").GetString() == epoch;
        }
        catch (Exception error) when (error is HttpRequestException or WebSocketException or JsonException or InvalidOperationException or KeyNotFoundException or IOException or OperationCanceledException) { token.ThrowIfCancellationRequested(); return false; }
    }
    private static async Task<JsonDocument> Receive(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[4096]; using var data = new MemoryStream();
        ValueWebSocketReceiveResult result;
        do { result = await ws.ReceiveAsync(buffer.AsMemory(), token).ConfigureAwait(false); if (result.MessageType != WebSocketMessageType.Text || data.Length + result.Count > 16384) throw new IOException("Unexpected handshake message"); data.Write(buffer, 0, result.Count); } while (!result.EndOfMessage);
        return JsonDocument.Parse(data.ToArray());
    }
}
