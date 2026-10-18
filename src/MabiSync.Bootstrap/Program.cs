// Built against the .NET Framework included with Windows 10/11. It keeps the
// application's bundled runtime unloaded while switching a prepared update.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("MabiSync")]
[assembly: System.Reflection.AssemblyProduct("MabiSync")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

internal static class Bootstrap
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4194304 };
    private static string Root;
    private static string Cache;

    [STAThread]
    private static int Main(string[] args)
    {
        Root = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        Cache = Path.Combine(Root, "Updates");
        try
        {
            Plain(Root);
            using (var mutex = new Mutex(false, "Local\\MabiSync-" + Digest(Encoding.UTF8.GetBytes(Root.ToUpperInvariant())).Substring(0, 24)))
            {
                bool held;
                try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
                if (!held) { MessageBox.Show("此目录中的 MabiSync 已在运行，请使用已打开的窗口。", "MabiSync"); return 0; }
                try
                {
                    if (args.Length == 0) InstallPending();
                    using (var app = StartApp(args)) { app.WaitForExit(); return app.ExitCode; }
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        catch (Exception error)
        {
            MessageBox.Show("MabiSync 未能启动：" + error.Message + "\n请将完整发布包解压到新文件夹后重试。", "MabiSync", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void InstallPending()
    {
        if (!Directory.Exists(Cache)) return;
        Plain(Cache);
        var marker = Path.Combine(Cache, "applying");
        var pending = Path.Combine(Cache, "pending.json");
        var backup = Path.Combine(Cache, "backup");
        try
        {
            if (File.Exists(marker))
            {
                Restore(backup);
                File.Delete(marker);
                File.Delete(pending);
                DiscardStage();
                Result(false, "", "上次更新未完成，已恢复原版本。");
                return;
            }
            if (!File.Exists(pending)) return;
            Plain(pending);
            var manifest = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(pending, Encoding.UTF8));
            var stage = Path.Combine(Cache, "staged");
            var version = Validate(stage, manifest);
            if (Directory.Exists(backup)) { PlainTree(backup); Directory.Delete(backup, true); }
            Directory.CreateDirectory(backup);
            File.WriteAllText(marker, version);
            foreach (var name in new[] { "App", "Docs" })
            {
                var live = Path.Combine(Root, name);
                var next = Path.Combine(stage, name);
                PlainTree(live);
                if (Directory.Exists(live)) Directory.Move(live, Path.Combine(backup, name));
                if (Directory.Exists(next)) Directory.Move(next, live);
            }
            var report = Path.Combine(Cache, "health.json");
            File.Delete(report);
            using (var check = StartApp(new[] { "--check-package", report }))
            {
                if (!check.WaitForExit(30000)) { check.Kill(); check.WaitForExit(5000); throw new IOException("新版启动检查超时。"); }
                if (check.ExitCode != 0 || !File.Exists(report)) throw new IOException("新版启动检查未通过。");
            }
            var health = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(report));
            if (!health.ContainsKey("started") || !Equals(health["started"], true) || !health.ContainsKey("version") || !Equals(health["version"], version)) throw new IOException("新版启动检查无效。");
            File.Delete(pending);
            File.Delete(marker); // Commit only after the new runtime passed its isolated startup check.
            Result(true, version, "");
            try { Directory.Delete(backup, true); Directory.Delete(stage, true); File.Delete(report); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (Exception error)
        {
            if (File.Exists(marker))
            {
                Restore(backup);
                File.Delete(marker);
            }
            File.Delete(pending);
            DiscardStage();
            Result(false, "", "更新未完成，继续使用原版本。" + error.Message);
        }
    }

    private static string Validate(string stage, Dictionary<string, object> manifest)
    {
        PlainTree(stage);
        if (Convert.ToInt32(manifest["schema"]) != 1 || (string)manifest["product"] != "MabiSync" || Convert.ToInt32(manifest["bootstrapVersion"]) != 1)
            throw new IOException("更新清单不受支持。");
        var version = (string)manifest["version"];
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+$")) throw new IOException("更新版本无效。");
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Dictionary<string, object> file in (IEnumerable)manifest["files"])
        {
            var path = (string)file["path"];
            if (!(path.StartsWith("App/", StringComparison.Ordinal) || path.StartsWith("Docs/", StringComparison.Ordinal)) ||
                path.Split('/').Any(p => p.Length == 0 || p == "." || p == ".." || p.EndsWith(".") || p.EndsWith(" ") || p.IndexOfAny(new[] { '\\', ':', '\0' }) >= 0) || !expected.Add(path))
                throw new IOException("更新路径无效。");
            var source = Path.Combine(stage, path.Replace('/', Path.DirectorySeparatorChar));
            var hash = (string)file["sha256"];
            if (!File.Exists(source) || new FileInfo(source).Length != Convert.ToInt64(file["bytes"]) || !Hash(source).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("更新文件校验失败：" + path);
        }
        if (!expected.Contains("App/MabiSync.exe") || !expected.Contains("App/MabiSync.dll") || !expected.Contains("App/bootstrap-version.txt") ||
            Directory.GetFiles(stage, "*", SearchOption.AllDirectories).Length != expected.Count) throw new IOException("更新文件不完整。");
        return version;
    }

    private static void Restore(string backup)
    {
        PlainTree(backup);
        foreach (var name in new[] { "App", "Docs" })
        {
            var previous = Path.Combine(backup, name);
            if (!Directory.Exists(previous)) continue;
            var live = Path.Combine(Root, name);
            if (Directory.Exists(live)) { PlainTree(live); Directory.Delete(live, true); }
            Directory.Move(previous, live);
        }
    }
    private static Process StartApp(string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(Root, "App", "MabiSync.exe"), string.Join(" ", args.Select(Quote)))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Root };
        return Process.Start(info);
    }
    private static string Quote(string value)
    {
        var text = new StringBuilder("\""); int slashes = 0;
        foreach (var item in value)
        {
            if (item == '\\') { slashes++; continue; }
            text.Append('\\', item == '"' ? slashes * 2 + 1 : slashes);
            text.Append(item); slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }
    private static void Result(bool success, string version, string message)
    { File.WriteAllText(Path.Combine(Cache, "result.json"), Json.Serialize(new { success = success, version = version, message = message }), Encoding.UTF8); }
    private static void DiscardStage()
    {
        var stage = Path.Combine(Cache, "staged");
        try { if (Directory.Exists(stage)) { PlainTree(stage); Directory.Delete(stage, true); } } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private static string Hash(string path) { using (var input = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", ""); }
    private static string Digest(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""); }
    private static void Plain(string path)
    {
        for (var item = Path.GetFullPath(path); item != null; item = Path.GetDirectoryName(item))
            if ((Directory.Exists(item) || File.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new IOException("程序目录包含链接，请使用普通解压目录。");
    }
    private static void PlainTree(string path)
    {
        Plain(path);
        if (!Directory.Exists(path)) return;
        // Inspect a directory before recursing, so a link cannot lead outside the installation.
        foreach (var entry in Directory.GetFileSystemEntries(path)) { Plain(entry); if (Directory.Exists(entry)) PlainTree(entry); }
    }
}
