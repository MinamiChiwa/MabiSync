using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MabiSync.Core;
using MabiSync.Windows;
using KeyBinding = MabiSync.Core.KeyBinding;

namespace MabiSync.App;

public partial class MainWindow
{
    private readonly WindowsGamePlatform _gamePlatform = new();
    private readonly GameInputExecutor _gameInput;
    private readonly InputRouter _inputRouter;
    private GameInputProfile? _gameProfile;
    private LocalStopHotkey? _hotkey;
    private readonly CancellationTokenSource _calibrationCancellation = new();
    private CancellationTokenSource? _currentCalibration;
    private readonly Dictionary<string, ModeBindings> _modeBindings = new() { ["emote"] = new(new ChatDraftBinding()), ["independent"] = new(), ["ensemble"] = new() };
    private bool _editingBindings, _calibrating;
    private string? _renderedMode;
    private string? _bindingError;
    private ButtonBinding? _startButtonDraft, _stopButtonDraft;
    private GameWindowId? SelectedWindow => (GameWindowBox.SelectedItem as WindowChoice)?.State.Identity;
    private Task PrepareWindowForRoundAsync(ScheduledAction action,CancellationToken token)=>Dispatcher.InvokeAsync(async()=>
    {
        token.ThrowIfCancellationRequested();
        if(_closing)throw new OperationCanceledException();
        if(_inputRouter.UseSimulation)return;
        var window=Volatile.Read(ref _gameProfile)?.Window??throw new InvalidOperationException("请先选择本机游戏窗口。");
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(token,_calibrationCancellation.Token);
        Feedback("正在恢复并激活本机游戏窗口……",FeedbackTone.Info);
        await _gamePlatform.ActivateAsync(window,cancellation.Token);
        Feedback("游戏窗口已就绪，等待本轮开始。",FeedbackTone.Info);
    }).Task.Unwrap();
    private void InitializeInputs()
    {
        _inputRouter.UseSimulation=EnableGameInput.IsChecked!=true;
        if (_registerSystemHotkey) _hotkey = new(StopLocally);
        RenderBindings();
        ReloadInstruments();
        if (_registerSystemHotkey && _hotkey?.Registered != true) MessageText.Text = "F9已被其他程序占用，本机停止仍可使用窗口按钮。";
    }
    private void RefreshInputs(bool active)
    {
        if (_renderedMode != SelectedMode) RenderBindings();
        InputControls.IsEnabled = !active && !_calibrating;
        LocalActionSummary.Text=_inputRouter.UseSimulation?"模拟模式":(GameWindowBox.SelectedItem as WindowChoice)?.State is{} selected
            ?string.IsNullOrWhiteSpace(selected.Title)?System.IO.Path.GetFileName(selected.Identity.ImagePath):selected.Title:"选择窗口并设置绑定";
        LocalActionSummary.ToolTip=(GameWindowBox.SelectedItem as WindowChoice)?.Label;
        ReadyButton.IsEnabled &= !_calibrating;
        StartButton.IsEnabled &= !_calibrating;
        StopBindingControls.Visibility = SelectedMode == "emote" ? Visibility.Collapsed : Visibility.Visible;
        StartBindingControls.Visibility = StartCalibrationText.Visibility = IsManualEnsembleJoiner ? Visibility.Collapsed : Visibility.Visible;
        GameBindingSettings.Visibility = _inputRouter.UseSimulation ? Visibility.Collapsed : Visibility.Visible;
        InputBadge.Text = _inputRouter.UseSimulation ? "模拟模式" : "游戏输入已开启";
        InputBadgeCard.Background=ColorBrush(_inputRouter.UseSimulation?"#203552":"#173D32");InputBadge.Foreground=ColorBrush(_inputRouter.UseSimulation?"#9DC7FF":"#86E1B2");
        InputStateText.Text = _bindingError ?? (_inputRouter.UseSimulation ? "模拟模式可先检查联机与倒计时。准备实测时开启“操作游戏”。" :
            SelectedMode == "emote" ? "F9：取消本机待触发任务。" : "F9：本机停止。");
    }
    private void RenderBindings()
    {
        _editingBindings = true;
        try
        {
            var binding = _modeBindings[SelectedMode]; _renderedMode = SelectedMode;
            _startButtonDraft = binding.Start as ButtonBinding; _stopButtonDraft = binding.Stop as ButtonBinding;
            ChatDraftItem.Visibility = SelectedMode == "emote" ? Visibility.Visible : Visibility.Collapsed;
            StartInputKind.SelectedIndex = binding.Start switch { ButtonBinding => 1, ChatDraftBinding => 2, _ => 0 };
            StopInputKind.SelectedIndex = binding.Stop is ButtonBinding ? 1 : 0;
            StartKeyBox.Text = binding.Start is KeyBinding start ? ShortcutText.Format(start) : "";
            StopKeyBox.Text = binding.Stop is KeyBinding stop ? ShortcutText.Format(stop) : "";
            StopIsToggleBox.IsChecked = binding.StopIsToggle;
            UpdateBindingDisplay();
        }
        finally { _editingBindings = false; }
        RebuildGameProfile();
    }
    private void UpdateBindingDisplay()
    {
        StartKeyBox.IsEnabled = StartInputKind.SelectedIndex == 0;
        StartKeyBox.Visibility = StartInputKind.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        StopKeyBox.Visibility = StopInputKind.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        StopKeyBox.IsEnabled = StopInputKind.SelectedIndex == 0;
        CaptureStartButton.Visibility = StartInputKind.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        CaptureStopButton.Visibility = StopInputKind.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        static string Describe(ButtonBinding? b) => b is null ? "点击“点选按钮位置”，再点选游戏中的按钮。右键、Esc 或 F9 取消。" : $"已记录 ({b.X}, {b.Y}) · {b.ClientWidth}×{b.ClientHeight} · DPI {b.Dpi}。窗口尺寸或缩放改变后重新记录。";
        StartCalibrationText.Text = StartInputKind.SelectedIndex switch { 1 => Describe(_startButtonDraft), 2 => "在游戏聊天框预填动作指令，回到工具点击本机就绪；开始前自动激活游戏，保持输入框打开，等待自动回车。", _ => "" };
        StopCalibrationText.Text = StopInputKind.SelectedIndex == 1 ? Describe(_stopButtonDraft) : "";
    }
    private void ShortcutFocused(object sender, KeyboardFocusChangedEventArgs e) => ShortcutHint.Text = "请直接按下快捷键，可同时按 Ctrl / Alt / Shift。Tab 离开录入框；F9 是本机停止键。";
    private void ShortcutBlurred(object sender, KeyboardFocusChangedEventArgs e) => ShortcutHint.Text = "";
    private void ShortcutKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift) return;
        e.Handled = true;
        if (e.IsRepeat || key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt) return;
        try
        {
            var mods = Keyboard.Modifiers;
            if (mods.HasFlag(ModifierKeys.Windows)) throw new FormatException("不支持 Windows 键组合，请使用其他按键。");
            var binding = ShortcutText.FromVirtualKey((ushort)KeyInterop.VirtualKeyFromKey(key), mods.HasFlag(ModifierKeys.Control), mods.HasFlag(ModifierKeys.Alt), mods.HasFlag(ModifierKeys.Shift));
            ((TextBox)sender).Text = ShortcutText.Format(binding);
            InputChanged();
            ShortcutHint.Text = "已记录 " + ShortcutText.Format(binding) + "，设置已自动保存。";
        }
        catch (FormatException error) { ShortcutHint.Text = error.Message; }
    }
    private void InputTextChanged(object sender, TextChangedEventArgs e) => InputChanged();
    private void InputSettingsChanged(object sender, RoutedEventArgs e) => InputChanged();
    private void InputSettingsChanged(object sender, SelectionChangedEventArgs e) => InputChanged();
    private void InputChanged()
    {
        if (!_uiReady || _editingBindings || _closing) return;
        InvalidateReadiness();
        _bindingError = null;
        try
        {
            var start = IsManualEnsembleJoiner ? _modeBindings[SelectedMode].Start : StartInputKind.SelectedIndex switch { 1 => (GameBinding?)_startButtonDraft, 2 when SelectedMode == "emote" => new ChatDraftBinding(), _ => ShortcutText.Parse(StartKeyBox.Text) };
            var stop = SelectedMode == "emote" ? null : StopInputKind.SelectedIndex == 1 ? (GameBinding?)_stopButtonDraft : ShortcutText.Parse(StopKeyBox.Text);
            _modeBindings[SelectedMode] = new(start, stop, SelectedMode != "emote" && (StopIsToggleBox.IsChecked == true || (start is not null && start == stop)));
        }
        catch (FormatException error) { _bindingError = error.Message; _modeBindings[SelectedMode] = new(); }
        _inputRouter.UseSimulation = EnableGameInput.IsChecked != true;
        UpdateBindingDisplay(); RebuildGameProfile(); SaveSettings(); Refresh();
    }
    private void GameWindowChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _closing) return;
        InvalidateReadiness(); RebuildGameProfile(); Refresh();
    }
    private void RebuildGameProfile()
    {
        Volatile.Write(ref _gameProfile, SelectedWindow is { } window ? new(window, _modeBindings["emote"], _modeBindings["independent"], _modeBindings["ensemble"]) : null);
    }
    private void SelectCaptureGameWindow(GameWindowId game)
    {
        if (_closing || SelectedWindow == game) return;
        var state = _gamePlatform.Inspect(game);
        if (state is null || state.Identity != game)
        { Feedback("所选游戏窗口已失效，请刷新窗口列表。", FeedbackTone.Warning); return; }
        var windows = _gamePlatform.ListGameWindows(ShowOtherWindows.IsChecked == true).ToList();
        if (windows.All(window => window.Identity != game)) windows.Add(state);
        var choices = windows.Select(window => new WindowChoice(window)).ToArray();
        GameWindowBox.ItemsSource = choices;
        GameWindowBox.SelectedItem = choices.Single(window => window.State.Identity == game);
        Feedback("已同步操作窗口：" + state.Title, FeedbackTone.Info);
    }
    private void RefreshWindowsClick(object sender, RoutedEventArgs e)
    {
        if(!_uiReady || _closing)return;
        var previous = SelectedWindow;
        var windows = _gamePlatform.ListGameWindows(ShowOtherWindows.IsChecked==true).Select(w => new WindowChoice(w)).ToArray();
        GameWindowBox.ItemsSource = windows;
        // Reuse only the exact live window. Never silently choose a new process after a restart.
        GameWindowBox.SelectedItem = windows.FirstOrDefault(w => w.State.Identity == previous);
        if (previous is null && windows.Length == 1) GameWindowBox.SelectedIndex = 0;
        if (windows.Length == 0) MessageText.Text = "未发现游戏窗口，请启动游戏后刷新，或勾选“显示其他窗口”。";
        else Feedback($"已发现 {windows.Length} 个游戏窗口。",FeedbackTone.Info);
    }
    private async void CaptureStartClick(object sender, RoutedEventArgs e) => await CaptureBinding((Button)sender, true);
    private async void CaptureStopClick(object sender, RoutedEventArgs e) => await CaptureBinding((Button)sender, false);
    private async Task CaptureBinding(Button button, bool start)
    {
        await Run(button, async () =>
        {
            var window = SelectedWindow ?? throw new InvalidOperationException("请先刷新并选择游戏窗口。");
            InvalidateReadiness();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_calibrationCancellation.Token);
            Volatile.Write(ref _currentCalibration, cancellation);
            _calibrating = true; Refresh();
            var restoreState=WindowState;
            try
            {
                Hide();
                await _gamePlatform.ActivateAsync(window,cancellation.Token);
                var state=_gamePlatform.Inspect(window)??throw new InvalidOperationException("游戏窗口已失效。");
                var picker=new ButtonPickerWindow(_gamePlatform,window,state.Client);
                var binding=await picker.PickAsync(cancellation.Token);
                if(binding is null){Feedback("已取消位置记录，原按钮设置已保留。",FeedbackTone.Info);return;}
                if (start) _startButtonDraft=binding; else _stopButtonDraft=binding;
                Feedback($"已记录{(start?"开始":"停止")}按钮位置 ({binding.X}, {binding.Y})，设置已保存。");
            }
            catch(OperationCanceledException){Feedback("已取消位置记录，原按钮设置已保留。",FeedbackTone.Info);return;}
            finally { Volatile.Write(ref _currentCalibration, null); _calibrating = false;if(!_closing){Show();WindowState=restoreState;Activate();} }
            InputChanged();
        });
    }
    private void ValidateLocalReadiness()
    {
        if (_calibrating) throw new InvalidOperationException("正在记录按钮位置，请完成后再就绪。");
        if (_inputRouter.UseSimulation) return;
        if (_bindingError is not null) throw new InvalidOperationException(_bindingError);
        if (_hotkey?.Registered != true) throw new InvalidOperationException("F9停止键不可用，请先解除其他程序的F9占用后重启工具。");
        var bindings = _modeBindings[SelectedMode];
        if (SelectedMode == "emote" && bindings.Start is ChatDraftBinding) _gameInput.ConfirmChatDraft();
        if (SelectedMode != "emote" && (bindings.Stop is null || bindings.StopIsToggle))
            throw new InvalidOperationException("普通演奏输入目前需要专用停止绑定。开停切换仍待实时演奏状态接入，暂不能用于自动停止。");
        foreach (var kind in SelectedMode == "emote" ? new[] { "start" } : IsManualEnsembleJoiner ? new[] { "stop" } : new[] { "start", "stop" })
        {
            var check = new ScheduledAction(Guid.NewGuid().ToString(), "readiness-only", kind, 0, "local") { Mode = SelectedMode };
            var preparation = _gameInput.Prepare(check); _gameInput.Forget(check.Id);
            if (!preparation.Ready) throw new InvalidOperationException(preparation.Error);
        }
    }
    private void InvalidateReadiness()
    {
        _client.CancelLocalPreparations();
        _inputRouter.StartsEnabled = false;
        _gameInput.RevokeChatDraft();
        _scheduler.CancelPendingStarts();
        if (ViewSession is not { } identity || !ViewConnected || ViewRoom is not { } room ||
            !room.GetProperty("members").EnumerateArray().Any(m => m.GetProperty("id").GetString() == identity.MemberId && m.GetProperty("ready").GetBoolean())) return;
        _ = NotifyLocalState("set-ready");
    }
    private async Task NotifyLocalState(string type)
    {
        try
        {
            await _client.SendAsync(new { type, ready = false, capabilities = new { simulation = _inputRouter.UseSimulation, canStart = false, canStop = false, timingOffsetMs = _scheduler.TimingOffsetMs, readyMode = _inputRouter.ReadyMode } });
        }
        catch (Exception error) { if (!_closing) _ = Dispatcher.BeginInvoke(() => MessageText.Text = error.Message); }
    }
    private void StopLocally()
    {
        _client.CancelLocalPreparations();
        _inputRouter.StartsEnabled = false; _gameInput.RevokeChatDraft();
        try { Volatile.Read(ref _currentCalibration)?.Cancel(); } catch (ObjectDisposedException) { }
        _scheduler.CancelAll();
        if (ViewRoom is { } room && room.GetProperty("performance") is { ValueKind: JsonValueKind.Object } p &&
            p.GetProperty("mode").GetString() is "independent" or "ensemble" && p.GetProperty("phase").GetString() is not ("cancelled" or "stopped") && ViewSession is { } identity)
            _scheduler.StopNow(Guid.NewGuid().ToString(), p.GetProperty("id").GetString()!, identity.ServerEpoch, p.GetProperty("mode").GetString()!, localOnly: true);
        if (ViewConnected) _ = NotifyLocalState("local-stop");
    }
    private void LoadInputSettings(JsonElement bindings)
    {
        var saved = bindings.Deserialize<Dictionary<string, ModeBindings>>() ?? [];
        saved["ensemble"] = MusicBindingSettings.Restore(saved);
        foreach (var mode in new[] { "emote", "independent", "ensemble" }) if (saved.TryGetValue(mode, out var binding) && binding is not null)
            _modeBindings[mode] = mode == "emote" ? new(binding.Start) : binding with { StopIsToggle = binding.StopIsToggle || (binding.Start is not null && binding.Start == binding.Stop) };
    }
    private sealed record WindowChoice(GameWindowState State)
    {
        public string Label => State.Label;
    }
}
