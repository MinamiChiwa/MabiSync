using System.IO;
using System.Text.Json;

namespace MabiSync.App;

internal static class AppFaultLog
{
    private static readonly object Gate = new();
    private static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MabiSync", "logs");
    internal static void Record(Exception error, string stage, string? directory = null)
    {
        try
        {
            lock (Gate)
            {
                directory ??= DefaultDirectory;
                Directory.CreateDirectory(directory);
                var file = Path.Combine(directory, "errors-" + DateTime.UtcNow.ToString("yyyyMMdd") + ".jsonl");
                if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024) File.Move(file, file + ".previous", true);
                var errors = new List<object>();
                for (Exception? current = error; current is not null && errors.Count < 8; current = current.InnerException)
                {
                    var stack = current.StackTrace ?? "";
                    errors.Add(new { type = current.GetType().FullName, current.HResult, stack = stack[..Math.Min(stack.Length, 32000)] });
                }
                // Exception messages and application state can contain addresses or score
                // content. Keep only exception types and code locations by default.
                File.AppendAllText(file, JsonSerializer.Serialize(new { utc = DateTime.UtcNow, stage, errors }) + Environment.NewLine);
                foreach (var old in new DirectoryInfo(directory).GetFiles("errors-*").OrderByDescending(f => f.LastWriteTimeUtc).Skip(6)) old.Delete();
            }
        }
        catch (Exception) { /* A diagnostic write must not replace the original error. */ }
    }
    internal static JsonElement[] ReadRecent(string? directory = null)
    {
        try
        {
            directory ??= DefaultDirectory;
            if (!Directory.Exists(directory)) return [];
            var events = new List<JsonElement>();
            foreach (var file in new DirectoryInfo(directory).GetFiles("errors-*").OrderByDescending(f => f.LastWriteTimeUtc).Take(2).Reverse())
                foreach (var line in File.ReadLines(file.FullName).TakeLast(20))
                    try { using var json = JsonDocument.Parse(line); events.Add(json.RootElement.Clone()); } catch (JsonException) { }
            return events.TakeLast(20).ToArray();
        }
        catch (Exception) { return []; }
    }
}
