using System.Windows;
using System.IO;
using System.Text.Json;

namespace MabiSync.App;
public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) => AppFaultLog.Record(e.Exception, "dispatcher");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception error) AppFaultLog.Record(error, "unhandled"); };
        TaskScheduler.UnobservedTaskException += (_, e) => AppFaultLog.Record(e.Exception, "unobserved-task");
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args is ["--check-package", var report])
        {
            try
            {
                var root = AppContext.BaseDirectory;
                foreach (var file in new[] { "MabiSync.dll", "coreclr.dll", "PresentationFramework.dll", "runtime/node.exe", "runtime/cloudflared.exe", "Service/server/index.js" })
                    if (!File.Exists(Path.Combine(root, file))) throw new FileNotFoundException("Package component missing");
                var catalogPath=Path.Combine(Directory.GetParent(root.TrimEnd(Path.DirectorySeparatorChar))!.FullName,"Config","乐器列表.txt");
                var instruments=MabiSync.Core.InstrumentCatalog.Merge(File.ReadAllLines(catalogPath),Array.Empty<string>());
                File.WriteAllText(report, JsonSerializer.Serialize(new { started = true, baseDirectory = root, instrumentCount=instruments.Count, wpf = typeof(Window).Assembly.GetName().Version?.ToString(), gameInputs = 0 }));
                Shutdown(0);
            }
            catch (Exception error) { AppFaultLog.Record(error, "package-startup"); Shutdown(1); }
            return;
        }
        base.OnStartup(e);
    }
}
