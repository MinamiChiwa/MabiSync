using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MabiSync.Client;

namespace MabiSync.App;

public partial class MainWindow
{
    private static readonly HttpClient UpdateHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly GitHubUpdates _updates = new(UpdateHttp);
    private readonly CancellationTokenSource _updateLifetime = new();
    private Window? _updateWindow;
    private bool _updating;
    private static string ApplicationVersion => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(App).Assembly)?.InformationalVersion.Split('+')[0]
        ?? typeof(App).Assembly.GetName().Version?.ToString(3) ?? "未知版本";
    private static string InstallationDirectory => Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;

    private void InitializeUpdates()
    {
        Closed += (_, _) => { _updateLifetime.Cancel(); _updateLifetime.Dispose(); };
        Loaded += (_, _) => ReadUpdateResult();
    }
    private async void CheckUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_updating || _closing) return;
        if (_updateWindow is not null) { _updateWindow.Activate(); return; }
        if (File.Exists(Path.Combine(InstallationDirectory, "Updates", "pending.json")))
        { Feedback("更新已下载，下次打开工具时生效。", FeedbackTone.Info); return; }
        _updating = true;
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = "检查中…";
        try
        {
            var release = await _updates.CheckAsync(ApplicationVersion, _updateLifetime.Token);
            if (_closing) return;
            if (release.Available) ShowUpdate(release);
            else Feedback($"当前 v{ApplicationVersion} 已是最新版本。", FeedbackTone.Info);
        }
        catch (Exception error) { if (!_closing) Feedback(UpdateError(error), FeedbackTone.Warning); }
        finally { _updating = false; if (!_closing) { CheckUpdateButton.Content = "检查更新"; CheckUpdateButton.IsEnabled = true; } }
    }
    private void ShowUpdate(ReleaseUpdate release)
    {
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = $"发现新版本 v{release.Version}", FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = $"当前版本 v{ApplicationVersion}", Foreground = ColorBrush("#A8B4C7"), Margin = new Thickness(0, 10, 0, 16) });
        var description = new TextBlock { Text = "由你选择是否下载。下载完成后，下次打开工具时自动更新；当前房间和演奏继续运行。", TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(description);
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 8, Margin = new Thickness(0, 14, 0, 8), Visibility = Visibility.Collapsed };
        panel.Children.Add(progress);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = ColorBrush("#9DC7FF"), Margin = new Thickness(0, 8, 0, 12) };
        panel.Children.Add(status);
        var download = new Button { Content = "下载更新", Style = (Style)FindResource("PrimaryAction") };
        download.Click += async (_, _) =>
        {
            if (_updating) return;
            _updating = true; download.IsEnabled = false; CheckUpdateButton.IsEnabled = false;
            progress.Visibility = Visibility.Visible;
            try
            {
                await _updates.PrepareAsync(release, InstallationDirectory, new Progress<UpdateProgress>(value =>
                {
                    if (_closing || !_updating) return;
                    status.Text = value.Stage; progress.Value = value.Percent;
                    CheckUpdateButton.Content = value.Percent is > 0 and < 100 ? $"下载中 {value.Percent}%" : "准备更新…";
                }), _updateLifetime.Token);
                if (_closing) return;
                status.Text = "更新已下载，下次打开工具时生效。";
                download.Content = "已下载";
                Feedback(status.Text, FeedbackTone.Success);
            }
            catch (Exception error) { if (!_closing) { status.Text = UpdateError(error); download.IsEnabled = true; Feedback(status.Text, FeedbackTone.Warning); } }
            finally { _updating = false; if (!_closing) { CheckUpdateButton.Content = "检查更新"; CheckUpdateButton.IsEnabled = true; } }
        };
        panel.Children.Add(download);
        var page = new Button { Content = "查看发布说明", ToolTip = "在浏览器中打开 GitHub 发布页" };
        page.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(release.Page.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception error) { Feedback("无法打开浏览器：" + error.Message, FeedbackTone.Warning); }
        };
        panel.Children.Add(page);
        _updateWindow = new Window { Title = "MabiSync 更新", Owner = this, Width = 480, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, Background = ColorBrush("#1A2331"), FontSize = 14, FontFamily = FontFamily, Content = panel };
        ConfigureAuxiliaryWindow(_updateWindow);
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        _updateWindow.Show();
    }
    private void ReadUpdateResult()
    {
        var path = Path.Combine(InstallationDirectory, "Updates", "result.json");
        try
        {
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var result = doc.RootElement;
            Feedback(result.GetProperty("success").GetBoolean() ? $"已更新到 v{ApplicationVersion}。" : result.GetProperty("message").GetString() ?? "更新未完成，继续使用原版本。",
                result.GetProperty("success").GetBoolean() ? FeedbackTone.Success : FeedbackTone.Warning);
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { Feedback("更新结果暂时无法读取。", FeedbackTone.Warning); }
    }
    private static string UpdateError(Exception error) => error switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } => "GitHub 查询次数已达上限，请稍后重试。",
        HttpRequestException => "连接 GitHub 失败，请确认网络后重试。",
        OperationCanceledException => "下载或检查已取消，或连接 GitHub 超时；可稍后重试。",
        UnauthorizedAccessException => "此目录无法写入更新，请将工具完整解压到可写入的文件夹。",
        JsonException => "更新信息暂不可用，请稍后重试。",
        _ => "更新未完成：" + error.Message
    };
}
