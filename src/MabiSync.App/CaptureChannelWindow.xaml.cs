using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using MabiSync.Core;
using MabiSync.Windows;

namespace MabiSync.App;

public partial class CaptureChannelWindow : Window
{
    private readonly GameWindowId? _preferred;
    private readonly GameObservationCapture _observer;
    private CancellationTokenSource? _cancellation;
    private Task? _check;
    private bool _closing, _closed, _refreshing;
    private CaptureDevice[] _devices = [];
    private IReadOnlyList<CaptureConnection> _connections = [];
    public CaptureChannelWindow(GameWindowId? game, GameObservationCapture observer)
    {
        InitializeComponent(); _preferred = game; _observer = observer;
        Loaded += (_, _) => { RefreshChannels(); ShowObservation(_observer.Current); }; Closing += OnClosing;
        _observer.Changed += ObservationChanged;
    }
    private void RefreshClick(object sender, RoutedEventArgs e) => RefreshChannels();
    private void RefreshChannels()
    {
        if (_check is { IsCompleted: false } || _observer.Current.Running) return;
        try
        {
            _refreshing = true;
            var previous = (GamesBox.SelectedItem as GameChoice)?.State.Identity ?? _preferred;
            var windows = new WindowsGamePlatform().ListGameWindows().Select(w => new GameChoice(w)).ToArray();
            GamesBox.ItemsSource = windows; GamesBox.SelectedItem = windows.FirstOrDefault(w => w.State.Identity == previous);
            if (GamesBox.SelectedItem is null && windows.Length == 1) GamesBox.SelectedIndex = 0;
            _devices = CaptureChannels.Devices().ToArray();
            CaptureStateText.Text = windows.Length == 0 ? "未发现可见游戏窗口。请恢复游戏后刷新。" : "请选择要校对的通道。";
            RefreshGameConnections();
        }
        catch (Exception error) { CaptureStateText.Text = Explain(error); CheckButton.IsEnabled = false; }
        finally { _refreshing = false; }
    }
    private void GameChanged(object sender, SelectionChangedEventArgs e) { if (!_refreshing) RefreshGameConnections(); }
    private void RefreshGameConnections()
    {
        try
        {
            _connections = GamesBox.SelectedItem is GameChoice game ? CaptureChannels.Connections(game.State.Identity.ProcessId) : [];
            var previous = (DevicesBox.SelectedItem as CaptureDevice)?.Name;
            var devices = _devices.Where(d => _connections.Any(c => d.Addresses.Contains(c.LocalAddress))).ToArray();
            DevicesBox.ItemsSource = devices; DevicesBox.SelectedItem = devices.FirstOrDefault(d => d.Name == previous);
            if (DevicesBox.SelectedItem is null && devices.Length > 0) DevicesBox.SelectedIndex = 0;
            RefreshConnectionSelection();
        }
        catch (Exception error) { CaptureStateText.Text = Explain(error); CheckButton.IsEnabled = false; }
    }
    private void DeviceChanged(object sender, SelectionChangedEventArgs e) => RefreshConnectionSelection();
    private void RefreshConnectionSelection()
    {
        var previous = ConnectionsBox.SelectedItem as CaptureConnection;
        var connections = DevicesBox.SelectedItem is CaptureDevice device ? _connections.Where(c => device.Addresses.Contains(c.LocalAddress)).ToArray() : [];
        ConnectionsBox.ItemsSource = connections; ConnectionsBox.SelectedItem = connections.FirstOrDefault(c => c == previous);
        if (ConnectionsBox.SelectedItem is null && connections.Length > 0) ConnectionsBox.SelectedIndex = 0;
        CheckButton.IsEnabled = connections.Length > 0 && _check is not { IsCompleted: false };
    }
    private async void CheckClick(object sender, RoutedEventArgs e)
    {
        if (_check is { IsCompleted: false } || GamesBox.SelectedItem is not GameChoice game || DevicesBox.SelectedItem is not CaptureDevice device || ConnectionsBox.SelectedItem is not CaptureConnection connection) return;
        _cancellation = new();
        SetRunning(true);
        try
        {
            var progress = new Progress<ChannelCheck>(s => { if (_closing) return; CaptureStateText.Text = s.Status; CountText.Text = $"收到的数据包：{s.Packets} · 已校对 {s.ElapsedSeconds:0.0} 秒"; });
            _check = CaptureChannels.CheckAsync(game.State.Identity, device, connection, progress, _cancellation.Token);
            await _check;
        }
        catch (OperationCanceledException) { CaptureStateText.Text = "校对已停止。"; }
        catch (Exception error) { CaptureStateText.Text = Explain(error); }
        finally { _cancellation.Dispose(); _cancellation = null; SetRunning(false); }
    }
    private void StopClick(object sender, RoutedEventArgs e) => _cancellation?.Cancel();
    private void SetRunning(bool running)
    {
        var observing = _observer.Current.Running;
        GamesBox.IsEnabled = DevicesBox.IsEnabled = ConnectionsBox.IsEnabled = RefreshButton.IsEnabled = !running && !observing;
        CancelButton.IsEnabled = running; CheckButton.IsEnabled = !running && !observing && ConnectionsBox.SelectedItem is CaptureConnection;
        ObserveButton.IsEnabled = !running && !observing && GamesBox.SelectedItem is GameChoice && DevicesBox.SelectedItem is CaptureDevice;
        StopObserveButton.IsEnabled = observing;
    }
    private void ObservationChanged(CaptureObservation state) => Dispatcher.BeginInvoke(() => { if (!_closing) ShowObservation(state); });
    private void ShowObservation(CaptureObservation state)
    {
        CaptureStateText.Text = state.Status;
        CountText.Text = $"持续观测数据包：{state.Packets}";
        IdentityText.Text = state.Game.Identity is { } identity ? $"已识别本机：{identity.Nickname}" + (state.Game.Concert is { } concert ? $" · 已核对 {concert.OrderedCharacterIds.Count} 名合奏成员" : " · 等待合奏成员事件")
            : "等待角色身份。启用识别后，请切换地图或重新登录角色。";
        var score = state.Game.Music;
        ScoreText.Visibility = score is null ? Visibility.Collapsed : Visibility.Visible;
        ScoreText.Text = score is null ? "" : $"乐谱：{score.Title} · " +
            (score.WholeScoreDurationSeconds is { } duration ? $"全谱 {duration:0.###} 秒" : "时长待解析") +
            (score.SelectedDurationSeconds is { } selected ? $" · 实际声部 {selected:0.###} 秒" : "") + "\n" + score.Issue;
        SetRunning(_check is { IsCompleted: false });
    }
    private async void ObserveClick(object sender, RoutedEventArgs e)
    {
        if (_check is { IsCompleted: false } || GamesBox.SelectedItem is not GameChoice game || DevicesBox.SelectedItem is not CaptureDevice device) return;
        try { await _observer.StartAsync(game.State.Identity, device); SetRunning(false); }
        catch (Exception error) { CaptureStateText.Text = Explain(error); }
    }
    private async void StopObserveClick(object sender, RoutedEventArgs e) { await _observer.StopAsync(); if (!_closing) ShowObservation(_observer.Current); }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true; if (_closing) return;
        _closing = true; _cancellation?.Cancel();
        _observer.Changed -= ObservationChanged;
        if (_check is not null) try { await _check; } catch { /* The visible check handler reports errors. */ }
        _closed = true;
        // Closing may complete synchronously when no check is active. Queue the
        // second Close after WPF has unwound this first Closing notification.
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
    private static string Explain(Exception error) => error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException
        ? "Npcap 不可用或版本不兼容，请检查安装后重试。"
        : "通道校对未完成：" + error.Message;
    private void NpcapDownloadClick(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try { Process.Start(new ProcessStartInfo("https://npcap.com/#download") { UseShellExecute = true }); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        { CaptureStateText.Text = "无法打开浏览器，请复制此地址下载 Npcap：https://npcap.com/#download"; }
    }
    private sealed record GameChoice(GameWindowState State)
    {
        public string Label => $"PID {State.Identity.ProcessId} · {State.Client.Width}×{State.Client.Height} · {System.IO.Path.GetDirectoryName(State.Identity.ImagePath)}";
    }
}
