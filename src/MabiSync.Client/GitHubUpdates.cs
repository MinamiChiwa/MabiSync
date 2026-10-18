using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MabiSync.Client;

public sealed record ReleaseAsset(string Name, Uri Url, long Size, string? Sha256);
public sealed record ReleaseUpdate(string CurrentVersion, string Version, bool Available, Uri Page, IReadOnlyList<ReleaseAsset> Assets);
public sealed record UpdateFile(string Path, long Bytes, string Sha256);
public sealed record UpdatePackage(string? FromVersion, string AssetName, string ArchivePrefix, string[] Paths, long Bytes = 0, string? Sha256 = null);
public sealed record UpdateManifest(int Schema, string Product, string Version, int BootstrapVersion, UpdateFile[] Files, UpdatePackage[] Packages);
public sealed record UpdateProgress(string Stage, int Percent);

/// <summary>Checks official releases; downloading is a separate, explicit operation.</summary>
public sealed class GitHubUpdates(HttpClient http)
{
    public const string Repository = "https://github.com/MinamiChiwa/MabiSync";
    public const string LatestApi = "https://api.github.com/repos/MinamiChiwa/MabiSync/releases/latest";
    public const string LatestManifest = Repository + "/releases/latest/download/MabiSync-update.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex Stable = new(@"^v?(\d+)\.(\d+)\.(\d+)$", RegexOptions.CultureInvariant);

    public async Task<ReleaseUpdate> CheckAsync(string currentVersion, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var directRequest = Request(new Uri(LatestManifest));
        using var directResponse = await http.SendAsync(directRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (directResponse.IsSuccessStatusCode)
        {
            using var descriptor = new MemoryStream();
            await ReadBoundedAsync(await directResponse.Content.ReadAsStreamAsync(timeout.Token), descriptor, 4 * 1024 * 1024, null, timeout.Token);
            var bytes = descriptor.ToArray();
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(bytes, Json) ?? throw new InvalidDataException("缺少更新清单。");
            if (manifest.Version is null || !Regex.IsMatch(manifest.Version, @"^\d+\.\d+\.\d+$")) throw new InvalidDataException("正式版本信息无效。");
            ValidateManifest(manifest, manifest.Version);
            var prefix = Repository + "/releases/download/v" + manifest.Version + "/";
            var assets = manifest.Packages.Select(p => new ReleaseAsset(p.AssetName, new Uri(prefix + p.AssetName), p.Bytes, p.Sha256)).ToList();
            if (assets.Any(a => a.Sha256 is null || !Regex.IsMatch(a.Sha256, "^[A-Fa-f0-9]{64}$"))) throw new InvalidDataException("更新包缺少校验信息。");
            assets.Add(new("MabiSync-update.json", new Uri(prefix + "MabiSync-update.json"), bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
            return new(currentVersion.Split('+')[0], manifest.Version, IsNewer(manifest.Version, currentVersion), new Uri(Repository + "/releases/tag/v" + manifest.Version), assets);
        }
        if (directResponse.StatusCode != HttpStatusCode.NotFound) directResponse.EnsureSuccessStatusCode();
        // Older releases have no descriptor. Fall back to the release API;
        // a shared-IP rate limit can still use GitHub's latest-page redirect.
        using var request = Request(new Uri(LatestApi));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            using var latestRequest = Request(new Uri(Repository + "/releases/latest"));
            using var latestResponse = await http.SendAsync(latestRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            latestResponse.EnsureSuccessStatusCode();
            var final = latestResponse.RequestMessage?.RequestUri?.AbsoluteUri ?? "";
            var prefix = Repository + "/releases/tag/";
            var tag = final.StartsWith(prefix, StringComparison.Ordinal) ? final[prefix.Length..] : "";
            if (!Stable.IsMatch(tag)) throw new InvalidDataException("暂时无法确定最新正式版。");
            var version = tag.TrimStart('v');
            return new(currentVersion.Split('+')[0], version, IsNewer(version, currentVersion), new Uri(final), []);
        }
        response.EnsureSuccessStatusCode();
        using var body = new MemoryStream();
        await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(timeout.Token), body, 4 * 1024 * 1024, null, timeout.Token);
        return ParseRelease(body.ToArray(), currentVersion);
    }

    public static ReleaseUpdate ParseRelease(ReadOnlyMemory<byte> json, string currentVersion)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Stable.IsMatch(tag) || root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("正式版本信息无效。");
        var version = tag.TrimStart('v');
        var page = TrustedUrl(root.GetProperty("html_url").GetString(), "/releases/tag/" + tag);
        var current = currentVersion.Split('+')[0];
        var available = IsNewer(version, current);
        var assets = new List<ReleaseAsset>();
        foreach (var item in root.GetProperty("assets").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString() ?? "";
            if (!Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,180}$")) continue;
            var url = TrustedUrl(item.GetProperty("browser_download_url").GetString(), "/releases/download/" + tag + "/" + name);
            var size = item.GetProperty("size").GetInt64();
            var digest = item.TryGetProperty("digest", out var value) ? value.GetString() : null;
            assets.Add(new(name, url, size, digest?.StartsWith("sha256:", StringComparison.Ordinal) == true ? digest[7..] : null));
        }
        return new(current, version, available, page, assets);
    }

    public async Task PrepareAsync(ReleaseUpdate release, string installDirectory, IProgress<UpdateProgress>? progress = null, CancellationToken token = default)
    {
        if (!release.Available) throw new InvalidOperationException("当前已是最新版本。");
        var root = Path.GetFullPath(installDirectory);
        if (!File.Exists(Path.Combine(root, "App", "MabiSync.exe")) || !File.Exists(Path.Combine(root, "App", "bootstrap-version.txt")))
            throw new InvalidOperationException("此版本尚不支持在线安装，请从发布页下载完整包。");
        EnsurePlainDirectory(root);
        var cache = Path.Combine(root, "Updates");
        EnsurePlainDirectory(cache);
        Directory.CreateDirectory(cache);
        using var downloadLock = new FileStream(Path.Combine(cache, "download.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(Path.Combine(cache, "pending.json"))) throw new InvalidOperationException("更新已下载，下次打开工具时生效。");
        foreach (var abandoned in Directory.GetDirectories(cache, "preparing-*"))
        { EnsurePlainDirectory(abandoned); Directory.Delete(abandoned, true); }
        var manifestAsset = Asset(release, "MabiSync-update.json");
        if (manifestAsset.Size > 4 * 1024 * 1024) throw new InvalidDataException("更新清单过大。");
        using var manifestBytes = new MemoryStream();
        progress?.Report(new("读取更新信息", 0));
        await DownloadAsync(manifestAsset, manifestBytes, null, token);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes.ToArray(), Json) ?? throw new InvalidDataException("缺少更新清单。");
        ValidateManifest(manifest, release.Version);
        if (File.ReadAllText(Path.Combine(root, "App", "bootstrap-version.txt")).Trim() != manifest.BootstrapVersion.ToString())
            throw new InvalidOperationException("此更新需要新版启动器，请从发布页下载完整包。");
        var package = await Task.Run(() => manifest.Packages.FirstOrDefault(p => p.FromVersion == release.CurrentVersion && CanReuse(root, manifest, p, token)), token)
            ?? manifest.Packages.FirstOrDefault(p => p.FromVersion is null)
            ?? throw new InvalidDataException("没有适合本机版本的更新包。");
        var packageAsset = Asset(release, package.AssetName);
        var work = Path.Combine(cache, "preparing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var archivePath = Path.Combine(work, "download.zip");
            using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await DownloadAsync(packageAsset, output, percent => progress?.Report(new(package.FromVersion is null ? "下载更新" : "下载增量更新", percent)), token);
            var staged = Path.Combine(work, "staged");
            Directory.CreateDirectory(staged);
            progress?.Report(new("校验并准备更新", 100));
            await Task.Run(() => Assemble(root, staged, archivePath, manifest, package, token), token);
            token.ThrowIfCancellationRequested();
            var destination = Path.Combine(cache, "staged");
            EnsurePlainDirectory(destination);
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            Directory.Move(staged, destination);
            var pending = Path.Combine(cache, "pending.json");
            await File.WriteAllBytesAsync(pending + ".tmp", manifestBytes.ToArray(), token);
            File.Move(pending + ".tmp", pending, true); // Published last; startup never installs an incomplete download.
            progress?.Report(new("已下载，下次打开工具时生效", 100));
        }
        finally { if (Directory.Exists(work)) Directory.Delete(work, true); }
    }

    public static void ValidateManifest(UpdateManifest manifest, string expectedVersion)
    {
        if (manifest.Schema != 1 || manifest.Product != "MabiSync" || manifest.Version != expectedVersion || manifest.BootstrapVersion != 1 ||
            manifest.Files is not { Length: > 0 and <= 2000 } || manifest.Packages is not { Length: > 0 and <= 32 })
            throw new InvalidDataException("更新清单版本不受支持。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null || !SafePath(file.Path) || !paths.Add(file.Path) || file.Bytes is < 0 or > 1024L * 1024 * 1024 ||
                file.Sha256 is null || !Regex.IsMatch(file.Sha256, "^[A-Fa-f0-9]{64}$")) throw new InvalidDataException("更新文件清单无效。");
            total += file.Bytes;
        }
        if (total > 2L * 1024 * 1024 * 1024 || !paths.Contains("App/MabiSync.exe") || !paths.Contains("App/MabiSync.dll") || !paths.Contains("App/bootstrap-version.txt"))
            throw new InvalidDataException("更新文件不完整。");
        foreach (var package in manifest.Packages)
            if (package is null || package.AssetName is null || !Regex.IsMatch(package.AssetName, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,180}$") || package.ArchivePrefix is null ||
                (package.ArchivePrefix != "" && !Regex.IsMatch(package.ArchivePrefix, "^[A-Za-z0-9._-]+/$")) || package.Paths is null ||
                package.Paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != package.Paths.Length || package.Paths.Any(p => !paths.Contains(p)) ||
                (package.FromVersion is null && package.Paths.Length != paths.Count)) throw new InvalidDataException("更新包清单无效。");
    }

    private static bool SafePath(string? path) => path is not null && (path.StartsWith("App/", StringComparison.Ordinal) || path.StartsWith("Docs/", StringComparison.Ordinal)) &&
        path.Split('/').All(s => s.Length > 0 && s is not "." and not ".." && !s.EndsWith('.') && !s.EndsWith(' ') && s.IndexOfAny(['\\', ':', '\0']) < 0);

    private static bool IsNewer(string version, string currentVersion)
    {
        var current = currentVersion.Split('+')[0];
        if (!System.Version.TryParse(current.Split('-')[0], out var installed)) throw new InvalidDataException("本机版本号无效。");
        var latest = System.Version.Parse(version.TrimStart('v'));
        return latest > installed || (latest == installed && current.Contains('-'));
    }

    private static bool CanReuse(string root, UpdateManifest manifest, UpdatePackage package, CancellationToken token)
    {
        var changed = package.Paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files.Where(f => !changed.Contains(f.Path)))
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(root, file.Path);
            EnsurePlainDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Bytes || !Hash(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static void Assemble(string root, string destination, string archivePath, UpdateManifest manifest, UpdatePackage package, CancellationToken token)
    {
        var expected = manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var changed = package.Paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000 || entry.FullName.Contains('\\') || entry.FullName.Split('/').Any(p => p is "." or ".." || p.Contains(':')))
                throw new InvalidDataException("更新包包含无效路径。");
            if (entry.FullName.EndsWith('/')) continue;
            if (!entry.FullName.StartsWith(package.ArchivePrefix, StringComparison.Ordinal)) throw new InvalidDataException("更新包目录不匹配。");
            var path = entry.FullName[package.ArchivePrefix.Length..];
            // Full portable packages also contain the immutable bootstrap and user-editable catalog.
            if (package.FromVersion is null && (path == "MabiSync.exe" || path == "Config/乐器列表.txt")) continue;
            if (!expected.TryGetValue(path, out var file) || !changed.Contains(path) || !found.Add(path) || entry.Length != file.Bytes)
                throw new InvalidDataException("更新包文件与清单不一致。");
            var outputPath = Path.Combine(destination, path);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using (var input = entry.Open()) using (var output = File.Create(outputPath)) CopyChecked(input, output, file.Bytes, token);
            if (!Hash(outputPath).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新文件校验失败。");
        }
        if (!found.SetEquals(changed)) throw new InvalidDataException("更新包缺少文件。");
        foreach (var file in manifest.Files.Where(f => !changed.Contains(f.Path)))
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(destination, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var source = Path.Combine(root, file.Path);
            EnsurePlainDirectory(source);
            using (var input = File.OpenRead(source)) using (var output = File.Create(path)) CopyChecked(input, output, file.Bytes, token);
            if (!Hash(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("本机文件已变化，请重新下载更新。");
        }
    }

    private static void CopyChecked(Stream input, Stream output, long expected, CancellationToken token)
    {
        var buffer = new byte[81920]; long count = 0; int read;
        while ((read = input.Read(buffer)) != 0) { token.ThrowIfCancellationRequested(); count += read; if (count > expected) throw new InvalidDataException("更新文件长度不符。"); output.Write(buffer, 0, read); }
        if (count != expected) throw new InvalidDataException("更新文件不完整。");
    }

    private async Task DownloadAsync(ReleaseAsset asset, Stream output, Action<int>? progress, CancellationToken token)
    {
        if (asset.Size is < 0 or > 1024L * 1024 * 1024 || asset.Sha256 is null || !Regex.IsMatch(asset.Sha256, "^[A-Fa-f0-9]{64}$"))
            throw new InvalidDataException("发布文件缺少有效校验信息。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        using var request = Request(asset.Url);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        var count = await ReadBoundedAsync(input, output, asset.Size, (buffer, read, total) => { hash.AppendData(buffer, 0, read); progress?.Invoke(asset.Size == 0 ? 100 : (int)(total * 100 / asset.Size)); }, timeout.Token);
        if (count != asset.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("下载校验失败，请重试。");
    }

    private static async Task<long> ReadBoundedAsync(Stream input, Stream output, long limit, Action<byte[], int, long>? chunk, CancellationToken token)
    {
        var buffer = new byte[81920]; long total = 0; int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0) { total += read; if (total > limit) throw new InvalidDataException("下载内容超出预期大小。"); await output.WriteAsync(buffer.AsMemory(0, read), token); chunk?.Invoke(buffer, read, total); }
        return total;
    }
    private static HttpRequestMessage Request(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MabiSync", "0.1"));
        if (uri.Host == "api.github.com") request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return request;
    }
    private static ReleaseAsset Asset(ReleaseUpdate release, string name) => release.Assets.SingleOrDefault(a => a.Name == name) ?? throw new InvalidDataException("该版本尚未提供在线更新包，请从发布页下载完整包。");
    private static Uri TrustedUrl(string? value, string suffix)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.AbsoluteUri != Repository + suffix) throw new InvalidDataException("更新链接并非官方仓库。");
        return uri;
    }
    private static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    public static void EnsurePlainDirectory(string path)
    {
        for (var item = Path.GetFullPath(path); item is not null; item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new IOException("更新目录包含链接，请使用普通解压目录。");
    }
}
