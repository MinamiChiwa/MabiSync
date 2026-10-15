using MabiSync.Client;

var tests = new List<(string Name, Func<Task> Run)>();
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
tests.Add(("Tunnel log parsing accepts only the exact temporary Cloudflare domain", () =>
{
    Check(QuickTunnel.ExtractEndpoint("| https://some-host.trycloudflare.com |")?.ToString() == "wss://some-host.trycloudflare.com/", "Valid endpoint missing");
    foreach (var line in new[] { "https://a.trycloudflare.com.evil.test", "https://trycloudflare.com", "http://a.trycloudflare.com", "https://a.trycloudflare.com@evil.test" })
        Check(QuickTunnel.ExtractEndpoint(line) is null, "Unrelated or deceptive endpoint accepted");
    return Task.CompletedTask;
}));
tests.Add(("An address alone is not ready; public product and epoch verification must pass", async () =>
{
    var launcher = new FakeLauncher(); var probe = new FakeProbe { BlockVerify = true };
    await using var tunnel = new QuickTunnel(launcher, probe, TimeSpan.FromSeconds(2));
    var start = tunnel.StartAsync("fixture.exe");
    await probe.Verifying.Task.WaitAsync(TimeSpan.FromSeconds(1));
    Check(tunnel.State.Status == "verifying" && tunnel.State.Endpoint is null, "Unverified URL exposed as ready");
    probe.Allow.SetResult(true); await start;
    Check(tunnel.State.Status == "ready" && probe.ExpectedEpoch == "epoch-fixture", "Public epoch not associated with local service");
    await tunnel.StartAsync("fixture.exe"); Check(launcher.Starts == 1, "Duplicate start replaced a healthy tunnel");
}));
tests.Add(("Cancelling startup closes its process and cannot later publish a ready address", async () =>
{
    var launcher = new FakeLauncher { EmitAddress = false }; var probe = new FakeProbe();
    await using var tunnel = new QuickTunnel(launcher, probe, TimeSpan.FromSeconds(2));
    var start = tunnel.StartAsync("fixture.exe");
    await launcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
    await tunnel.StopAsync(); await start;
    launcher.Emit("https://late.trycloudflare.com");
    Check(tunnel.State.Status == "off" && tunnel.State.Endpoint is null && launcher.Process!.Disposed, "Cancelled startup became ready or leaked its process");
}));
tests.Add(("Failed public identity checks time out and close the owned tunnel", async () =>
{
    var launcher = new FakeLauncher(); var probe = new FakeProbe { VerifyResult = false };
    await using var tunnel = new QuickTunnel(launcher, probe, TimeSpan.FromMilliseconds(100));
    await tunnel.StartAsync("fixture.exe");
    Check(tunnel.State.Status == "error" && tunnel.State.Endpoint is null && launcher.Process!.Disposed, "Unverified tunnel was left running");
}));
tests.Add(("Unexpected process exit clears an already verified invitation", async () =>
{
    var launcher = new FakeLauncher(); await using var tunnel = new QuickTunnel(launcher, new FakeProbe(), TimeSpan.FromSeconds(2));
    await tunnel.StartAsync("fixture.exe"); launcher.Process!.Exit.TrySetResult();
    for (var i = 0; i < 100 && tunnel.State.Status == "ready"; i++) await Task.Delay(5);
    Check(tunnel.State.Status == "error" && tunnel.State.Endpoint is null, "Dead tunnel kept a ready invitation");
}));
tests.Add(("A failed local service check never launches a public process", async () =>
{
    var launcher = new FakeLauncher(); await using var tunnel = new QuickTunnel(launcher, new FakeProbe { FailLocal = true }, TimeSpan.FromSeconds(2));
    await tunnel.StartAsync("fixture.exe");
    Check(tunnel.State.Status == "error" && launcher.Starts == 0, "Started a tunnel for an unknown local service");
}));
var failed = 0;
tests.Add(("A living tunnel with unreachable public endpoint withdraws its invitation and reports failure", async () =>
{
    var constructor=typeof(QuickTunnel).GetConstructors().Single();
    Check(constructor.GetParameters().Length==4,"Tunnel has no ongoing health validation");
    var launcher=new FakeLauncher();var probe=new FakeProbe();
    await using var tunnel=(QuickTunnel)constructor.Invoke([launcher,probe,TimeSpan.FromSeconds(2),TimeSpan.FromMilliseconds(10)]);
    await tunnel.StartAsync("fixture.exe");probe.VerifyResult=false;
    for(int i=0;i<100 && tunnel.State.Status=="ready";i++)await Task.Delay(10);
    Check(tunnel.State.Status=="error" && tunnel.State.Endpoint is null && !launcher.Process!.HasExited,"Unreachable tunnel retained its copyable invitation");
}));
tests.Add(("Closing during the local health request cannot launch a tunnel on its late reply", async () =>
{
    var launcher=new FakeLauncher();var probe=new FakeProbe {BlockLocal=true};var tunnel=new QuickTunnel(launcher,probe);
    var start=tunnel.StartAsync("fixture.exe");await probe.LocalStarted.Task;
    var dispose=tunnel.DisposeAsync().AsTask();probe.AllowLocal.TrySetResult("fixture");await Task.WhenAll(start,dispose);
    Check(launcher.Starts==0,"A late health response launched a process during shutdown");
}));
tests.Add(("Disposed public connections cannot start another tunnel", async () =>
{
    var launcher=new FakeLauncher();var tunnel=new QuickTunnel(launcher,new FakeProbe());
    await tunnel.DisposeAsync();
    try {await tunnel.StartAsync("fixture.exe");throw new InvalidOperationException("Disposed tunnel restarted");}
    catch(ObjectDisposedException) { }
    Check(launcher.Starts==0,"A disposed owner launched a new process");
}));
foreach (var (name, run) in tests) { try { await run(); Console.WriteLine("PASS " + name); } catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); } }
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed"); return failed == 0 ? 0 : 1;

sealed class FakeProcess : ITunnelProcess
{
    public readonly TaskCompletionSource Exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Id => 123; public bool HasExited => Exit.Task.IsCompleted; public Task Exited => Exit.Task; public bool Disposed;
    public ValueTask DisposeAsync() { Disposed = true; Exit.TrySetResult(); return ValueTask.CompletedTask; }
}
sealed class FakeLauncher : ITunnelLauncher
{
    public bool EmitAddress = true; public int Starts; public FakeProcess? Process;
    public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action<string>? _line;
    public ITunnelProcess Start(string executable, int port, Action<string> line) { Starts++; Process = new(); _line = line; if (EmitAddress) line("https://fixture.trycloudflare.com"); Started.TrySetResult(); return Process; }
    public void Emit(string line) => _line?.Invoke(line);
}
sealed class FakeProbe : IQuickTunnelProbe
{
    public bool BlockLocal;
    public readonly TaskCompletionSource LocalStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource<string> AllowLocal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool BlockVerify, FailLocal, VerifyResult = true; public string? ExpectedEpoch;
    public readonly TaskCompletionSource Verifying = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource<bool> Allow = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<string> LocalEpochAsync(int port, CancellationToken token) {LocalStarted.TrySetResult();return BlockLocal?AllowLocal.Task:FailLocal ? Task.FromException<string>(new IOException("Wrong local product")) : Task.FromResult("epoch-fixture");}
    public async Task<bool> VerifyAsync(Uri endpoint, string epoch, CancellationToken token) { ExpectedEpoch = epoch; Verifying.TrySetResult(); return BlockVerify ? await Allow.Task.WaitAsync(token) : VerifyResult; }
}
