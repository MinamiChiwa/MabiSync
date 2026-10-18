using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MabiSync.Client;

internal static class ReleaseChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static void AddTo(List<(string Name, Func<Task> Run)> tests)
    {
        tests.Add(("Update checking compares stable and preview versions and never downloads an asset", async () =>
        {
            using var fixture = new Fixture();
            var checker = new GitHubUpdates(fixture.Http);
            var release = await checker.CheckAsync("1.0.0");
            Check(release.Available && release.Version == "1.0.1" && fixture.Requests.SequenceEqual([GitHubUpdates.LatestManifest]), "Check downloaded a payload or missed a newer release");
            Check(!GitHubUpdates.ParseRelease(fixture.ReleaseBytes, "1.0.2-preview.1").Available, "Preview would downgrade to an older stable release");
            Check(GitHubUpdates.ParseRelease(fixture.ReleaseBytes, "1.0.1-preview.3+build").Available, "Stable release does not supersede its preview");
            Check(!GitHubUpdates.ParseRelease(fixture.ReleaseBytes, "1.0.1").Available, "Equal version offered an update");
        }));
        tests.Add(("Explicit download uses the matching delta and stages it without changing running files or user catalog", async () =>
        {
            using var fixture = new Fixture();
            var checker = new GitHubUpdates(fixture.Http);
            await checker.PrepareAsync(await checker.CheckAsync("1.0.0"), fixture.Root);
            Check(fixture.Requests.Any(p => p.EndsWith("delta.zip")) && !fixture.Requests.Any(p => p.EndsWith("full.zip")), "Delta was not selected");
            Check(File.ReadAllText(Path.Combine(fixture.Root, "App/MabiSync.dll")) == "old", "Running application was overwritten");
            Check(File.ReadAllText(Path.Combine(fixture.Root, "Config/乐器列表.txt")) == "user catalog", "User catalog was replaced");
            Check(File.Exists(Path.Combine(fixture.Root, "Updates/pending.json")), "Complete update was not committed as pending");
            foreach (var file in fixture.Target)
                Check(File.ReadAllBytes(Path.Combine(fixture.Root, "Updates/staged", file.Key)).SequenceEqual(file.Value), "Staged target differs: " + file.Key);
        }));
        tests.Add(("Changed local base files select a full online package instead of constructing a broken delta", async () =>
        {
            using var fixture = new Fixture();
            File.WriteAllText(Path.Combine(fixture.Root, "App/unchanged.bin"), "modified");
            var checker = new GitHubUpdates(fixture.Http);
            await checker.PrepareAsync(await checker.CheckAsync("1.0.0"), fixture.Root);
            Check(fixture.Requests.Any(p => p.EndsWith("full.zip")) && !fixture.Requests.Any(p => p.EndsWith("delta.zip")), "Invalid base still used delta");
            Check(File.ReadAllText(Path.Combine(fixture.Root, "Config/乐器列表.txt")) == "user catalog", "Full online update changed catalog");
        }));
        tests.Add(("Corrupt downloads and invalid zip paths never publish a pending update", async () =>
        {
            foreach (var failure in new[] { "digest", "zip-path", "file-hash", "missing" })
            {
                using var fixture = new Fixture(failure);
                var checker = new GitHubUpdates(fixture.Http);
                try { await checker.PrepareAsync(await checker.CheckAsync("1.0.0"), fixture.Root); throw new Exception("Bad update accepted: " + failure); }
                catch (InvalidDataException) { }
                Check(!File.Exists(Path.Combine(fixture.Root, "Updates/pending.json")) && File.ReadAllText(Path.Combine(fixture.Root, "App/MabiSync.dll")) == "old", "Bad download modified install");
            }
        }));
        tests.Add(("Cancelled downloads preserve the active app and leave no installable partial update", async () =>
        {
            using var fixture = new Fixture("cancel");
            var checker = new GitHubUpdates(fixture.Http);
            var release = await checker.CheckAsync("1.0.0");
            using var cancel = new CancellationTokenSource();
            var download = checker.PrepareAsync(release, fixture.Root, token: cancel.Token);
            await fixture.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel();
            try { await download; throw new Exception("Cancelled update completed"); } catch (OperationCanceledException) { }
            Check(!File.Exists(Path.Combine(fixture.Root, "Updates/pending.json")) && !Directory.GetDirectories(Path.Combine(fixture.Root, "Updates"), "preparing-*").Any(), "Partial download survived cancellation");
        }));
        tests.Add(("Legacy releases remain checkable when GitHub API requests are rate limited", async () =>
        {
            using var fixture = new Fixture("legacy-rate");
            var release = await new GitHubUpdates(fixture.Http).CheckAsync("1.0.0");
            Check(release.Available && release.Version == "1.0.1" && release.Page.AbsoluteUri.EndsWith("/releases/tag/v1.0.1"), "Rate limit became a false latest-version result");
            Check(fixture.Requests.SequenceEqual([GitHubUpdates.LatestManifest, GitHubUpdates.LatestApi, GitHubUpdates.Repository + "/releases/latest"]), "Legacy fallback fetched an update payload");
        }));
        tests.Add(("Update metadata rejects foreign links, prereleases and escaping manifest paths", () =>
        {
            using var fixture = new Fixture();
            foreach (var bytes in new[] {
                Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(fixture.ReleaseBytes).Replace("https://github.com/MinamiChiwa/MabiSync", "https://github.com/other/repo")),
                Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(fixture.ReleaseBytes).Replace("\"prerelease\":false", "\"prerelease\":true")) })
            { try { GitHubUpdates.ParseRelease(bytes, "1.0.0"); throw new Exception("Invalid release accepted"); } catch (InvalidDataException) { } }
            foreach (var path in new[] { "App/../outside", "App/a:stream", "Config/settings.json", "App/../../App/test" })
            {
                var manifest = fixture.Manifest with { Files = [new(path, 1, new string('A', 64))] };
                try { GitHubUpdates.ValidateManifest(manifest, "1.0.1"); throw new Exception("Unsafe path accepted"); } catch (InvalidDataException) { }
            }
            return Task.CompletedTask;
        }));
    }

    private sealed class Fixture : HttpMessageHandler
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "mabisync-update-" + Guid.NewGuid().ToString("N"));
        public readonly HttpClient Http;
        public readonly List<string> Requests = [];
        public readonly TaskCompletionSource Blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly Dictionary<string, byte[]> Target = new()
        { ["App/MabiSync.exe"] = "executable"u8.ToArray(), ["App/MabiSync.dll"] = "new"u8.ToArray(), ["App/bootstrap-version.txt"] = "1"u8.ToArray(), ["App/unchanged.bin"] = "retained runtime"u8.ToArray(), ["Docs/guide.txt"] = "updated guide"u8.ToArray() };
        public readonly UpdateManifest Manifest;
        public readonly byte[] ReleaseBytes;
        private readonly Dictionary<string, byte[]> _bodies = [];
        private readonly string? _failure;
        public Fixture(string? failure = null)
        {
            _failure = failure; Http = new HttpClient(this) { Timeout = Timeout.InfiniteTimeSpan };
            foreach (var pair in Target) { var path = Path.Combine(Root, pair.Key); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, pair.Value); }
            File.WriteAllText(Path.Combine(Root, "App/MabiSync.dll"), "old");
            Directory.CreateDirectory(Path.Combine(Root, "Config")); File.WriteAllText(Path.Combine(Root, "Config/乐器列表.txt"), "user catalog");
            var changed = new[] { "App/MabiSync.dll", "Docs/guide.txt" };
            Manifest = new(1, "MabiSync", "1.0.1", 1, Target.Select(p => new UpdateFile(p.Key, p.Value.Length, Hash(p.Value))).ToArray(),
                [new("1.0.0", "delta.zip", "", changed), new(null, "full.zip", "portable/", Target.Keys.ToArray())]);
            _bodies["delta.zip"] = Zip(changed.Where(p => failure != "missing" || p != "Docs/guide.txt").ToDictionary(p => p, p => failure == "file-hash" && p.EndsWith("dll") ? "bad"u8.ToArray() : Target[p]), "", failure == "zip-path");
            _bodies["full.zip"] = Zip(Target.Append(new KeyValuePair<string, byte[]>("Config/乐器列表.txt", "default catalog"u8.ToArray())).Append(new("MabiSync.exe", "bootstrap"u8.ToArray())).ToDictionary(), "portable/", false);
            Manifest = Manifest with { Packages = Manifest.Packages.Select(p => p with { Bytes = _bodies[p.AssetName].Length, Sha256 = failure == "digest" && p.AssetName == "delta.zip" ? new string('0', 64) : Hash(_bodies[p.AssetName]) }).ToArray() };
            _bodies["MabiSync-update.json"] = JsonSerializer.SerializeToUtf8Bytes(Manifest, Json);
            ReleaseBytes = JsonSerializer.SerializeToUtf8Bytes(new { tag_name = "v1.0.1", html_url = GitHubUpdates.Repository + "/releases/tag/v1.0.1", draft = false, prerelease = false,
                assets = _bodies.Select(p => new { name = p.Key, browser_download_url = GitHubUpdates.Repository + "/releases/download/v1.0.1/" + p.Key, size = p.Value.Length, digest = "sha256:" + (failure == "digest" && p.Key == "delta.zip" ? new string('0', 64) : Hash(p.Value)) }) });
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!.AbsoluteUri; Requests.Add(uri);
            if (_failure == "legacy-rate")
            {
                if (uri == GitHubUpdates.LatestManifest) return new(HttpStatusCode.NotFound);
                if (uri == GitHubUpdates.LatestApi) return new(HttpStatusCode.Forbidden);
                return new(HttpStatusCode.OK) { RequestMessage = new HttpRequestMessage(HttpMethod.Get, GitHubUpdates.Repository + "/releases/tag/v1.0.1") };
            }
            if (_failure == "cancel" && uri.EndsWith("delta.zip")) { Blocked.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(uri == GitHubUpdates.LatestApi ? ReleaseBytes : _bodies[Path.GetFileName(request.RequestUri.AbsolutePath)]) };
        }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
        private static byte[] Zip(Dictionary<string, byte[]> files, string prefix, bool escape)
        {
            using var bytes = new MemoryStream();
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            {
                foreach (var pair in files) using (var entry = zip.CreateEntry(prefix + pair.Key).Open()) entry.Write(pair.Value);
                if (escape) using (var entry = zip.CreateEntry("../escape").Open()) entry.WriteByte(1);
            }
            return bytes.ToArray();
        }
    }
}
