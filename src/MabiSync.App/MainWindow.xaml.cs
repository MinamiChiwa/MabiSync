using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MabiSync.Client;
using MabiSync.Core;
using MabiSync.Windows;
using Microsoft.Win32;

namespace MabiSync.App;

public partial class MainWindow : Window
{
    private readonly StopwatchClock _clock = new();
    private readonly RoomClock _roomClock;
    private readonly ActionScheduler _scheduler;
    private readonly RoomClient _client;
    private readonly LocalService _server = new();
    private readonly QuickTunnel _tunnel = new(new CloudflaredLauncher(), new QuickTunnelProbe());
    private readonly ObservableCollection<MemberRow> _members = [];
    private readonly DispatcherTimer _displayTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly List<ActionReceipt> _receipts = [];
    private readonly string _settings;
    private readonly bool _registerSystemHotkey;
    private bool _refreshing, _closing, _closed, _uiReady;
    private int _queued;
    public MainWindow() : this(null, true) { }
    internal MainWindow(string? settingsPath, bool registerSystemHotkey)
    {
        _settings = settingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MabiSync", "settings.json");
        _registerSystemHotkey = registerSystemHotkey;
        InitializeComponent();
        _roomClock = new(_clock);
        _gameInput = new(_clock, _gamePlatform, () => Volatile.Read(ref _gameProfile), new UnknownPlaybackEvidence());
        _inputRouter = new(new SimulationInput(_clock), _gameInput) { StartsEnabled = false };
        _scheduler = new(_clock, _roomClock, _inputRouter);
        _client = new(_clock, _roomClock, _scheduler);
        _client.PrepareLocalAsync=PrepareWindowForRoundAsync;
        _client.Changed += QueueRefresh;
        _tunnel.Changed += state => Dispatcher.BeginInvoke(() => TunnelChanged(state));
        _client.Notice += message => Dispatcher.BeginInvoke(() => MessageText.Text = Friendly(message));
        _client.RoomRestored += identity=>Dispatcher.BeginInvoke(()=>{
            if(!_closing && _client.Connected && _client.Session is{} current && current.MemberId==identity.MemberId && current.ServerEpoch==identity.ServerEpoch)
                Feedback("房间已重新连接，成员列表已更新。");
        });
        _client.ReceiptReported += receipt =>
        {
            if (!receipt.Simulated && Volatile.Read(ref _gameProfile)?.Emote.Start is ChatDraftBinding &&
                ViewRoom?.GetProperty("performance") is { ValueKind: JsonValueKind.Object } performance && performance.GetProperty("mode").GetString() == "emote")
            {
                _inputRouter.StartsEnabled = false; _gameInput.RevokeChatDraft();
                if (ViewConnected) _ = NotifyLocalState("set-ready");
            }
            Dispatcher.BeginInvoke(() => {
            _receipts.Add(receipt); if (_receipts.Count > 100) _receipts.RemoveAt(0);
            ReceiptBox.Text = string.Join(Environment.NewLine, _receipts.TakeLast(8).Select(r => $"{(r.Simulated ? "模拟操作" : "游戏操作")}：{ActionStatusText(r.Status)} · 本机微调 {r.AppliedTimingOffsetMs:+0;-0;0} ms · 执行偏差 {(r.ActualLocalMs - r.PlannedLocalMs)?.ToString("F1") ?? "—"} ms · {r.Detail}"));
            });
        };
        MembersGrid.ItemsSource = _members; LeaderBox.ItemsSource = _members;
        LoadLanAddresses();LoadSettings();
        _scheduler.TimingOffsetMs = TimingOffsetMs; RefreshTimingLabel();
        _uiReady = true;
        InitializeWindowActions();
        RefreshProvider();
        InitializeInputs();
        InitializeObservation();
        _displayTimer.Tick += (_, _) => RefreshStatus(); _displayTimer.Start();
        Loaded += (_, _) => { MinWidth=Math.Min(MinWidth,SystemParameters.WorkArea.Width-30);MinHeight=Math.Min(MinHeight,SystemParameters.WorkArea.Height-30);Width = Math.Min(Width, SystemParameters.WorkArea.Width - 30); Height = Math.Min(Height, SystemParameters.WorkArea.Height - 30); RefreshStartupWindows(); Refresh(); };
        SizeChanged+=(_,_)=>StatusCard.MaxHeight=Math.Max(200,ActualHeight*.48);
        Closing += OnClosing;
    }
    private void QueueRefresh()
    {
        if (_closing || Interlocked.Exchange(ref _queued, 1) != 0) return;
        Dispatcher.BeginInvoke(() => { Interlocked.Exchange(ref _queued, 0); if (!_closing) Refresh(); });
    }
    private void Refresh() => WithRoomView(RefreshView);
    private void RefreshView()
    {
        _refreshing = true;
        try
        {
            var session = ViewSession;
            var room = ViewRoom;
            if (session is not null) RoomTitle.Text = $"房间码 {session.Code} · {(ViewConnected ? "已连接" : "连接中断，可重连恢复身份")}";
            else RoomTitle.Text = "创建公网房间，或粘贴完整邀请加入。";
            if (room is { } value && session is not null)
            {
                if(value.TryGetProperty("mode",out var roomMode))ModeBox.SelectedIndex=roomMode.GetString()=="ensemble"?1:0;
                ConsumeEndedMusicReadiness(value, session.MemberId);
                foreach(var member in value.GetProperty("members").EnumerateArray())
                    if(member.TryGetProperty("instrument",out var instrument) && instrument.GetString() is{} name && !string.IsNullOrWhiteSpace(name) && !InstrumentOptions.Contains(name))InstrumentOptions.Add(name);
                var present = new HashSet<string>();
                foreach (var member in value.GetProperty("members").EnumerateArray())
                {
                    var id = member.GetProperty("id").GetString()!; present.Add(id);
                    var row = _members.FirstOrDefault(m => m.Id == id);
                    if (row is null) { row = new(id,RowSelectionChanged); _members.Add(row); }
                    row.Update(member,CanEditEnsemble,id==session.MemberId,SelectedMode=="ensemble");
                }
                foreach (var row in _members.Where(m => !present.Contains(m.Id)).ToArray()) _members.Remove(row);
                LeaderBox.SelectedValue = value.GetProperty("leaderId").GetString();
                var host = value.GetProperty("hostId").GetString() == session.MemberId;
                StartButton.IsEnabled = StopButton.IsEnabled = CancelButton.IsEnabled = LeaderBox.IsEnabled = host && ViewConnected;
                var self = value.GetProperty("members").EnumerateArray().FirstOrDefault(m => m.GetProperty("id").GetString() == session.MemberId);
                if (self.ValueKind == JsonValueKind.Object && self.TryGetProperty("gameIdentity", out var observedIdentity) && observedIdentity.ValueKind == JsonValueKind.Object)
                { var name = self.GetProperty("nickname").GetString(); if (!string.IsNullOrWhiteSpace(name) && NicknameBox.Text != name) NicknameBox.Text = name; }
                ReadyButton.Content = self.ValueKind == JsonValueKind.Object && self.GetProperty("ready").GetBoolean() ? "已就绪 · 取消" : "就绪";
                ContinueButton.IsEnabled = host && value.GetProperty("performance").ValueKind == JsonValueKind.Object && value.GetProperty("performance").GetProperty("phase").GetString() == "interrupted";
            }
            else { _members.Clear(); StartButton.IsEnabled = StopButton.IsEnabled = CancelButton.IsEnabled = ContinueButton.IsEnabled = LeaderBox.IsEnabled = false; }
            ReadyButton.IsEnabled = session is not null && room is not null && ViewConnected;
            RefreshFlow(); RefreshEnsembleRoster(); RefreshConnection(); RefreshStatus();
            foreach(var row in _members)if(!string.IsNullOrWhiteSpace(row.Instrument) && !InstrumentOptions.Contains(row.Instrument))InstrumentOptions.Add(row.Instrument);
        }
        finally { _refreshing = false; }
    }
    private string SelectedMode => (ModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "emote";
    private MusicStopRule SelectedStopRule => MusicStopSettings.Restore((StopChoice.SelectedItem as ComboBoxItem)?.Tag as string, null);
    private async void ModeChanged(object sender, SelectionChangedEventArgs e) {
        if(!_uiReady || _refreshing || _closing)return;
        if(sender==ModeBox){InvalidateReadiness();if(IsRoomHost && ViewConnected)await EditRoster(()=>_client.SetRoomModeAsync(SelectedMode),"本轮模式已同步给所有成员。");}
        else Feedback("已更新本轮结束方式。",FeedbackTone.Info);
        SaveSettings();Refresh();
    }
    private void RefreshFlow()
    {
        var performance = ViewRoom?.GetProperty("performance");
        var active = performance is { ValueKind: JsonValueKind.Object } && performance.Value.GetProperty("phase").GetString() is not ("cancelled" or "stopped");
        if (active) ModeBox.SelectedIndex = performance!.Value.GetProperty("mode").GetString() switch { "independent" or "ensemble" => 1, _ => 0 };
        ModeBox.IsEnabled = !active && (ViewSession is null || IsRoomHost) && _rosterEdits==0;
        TimingOffsetSlider.IsEnabled = TimingOffsetBox.IsEnabled = ResetTimingButton.IsEnabled = !active;
        var isHost = ViewSession is { } role && ViewRoom?.GetProperty("hostId").GetString() == role.MemberId;
        StopChoice.IsEnabled = DurationBox.IsEnabled = !active && (isHost || ViewSession is null);
        RefreshInputs(active);
        var emote = SelectedMode == "emote";
        ModeSummaryText.Text=emote?"表情动作":"合奏";
        var musicVisibility = emote ? Visibility.Collapsed : Visibility.Visible;
        LeaderControls.Visibility = StopControls.Visibility = StopButton.Visibility = ContinueButton.Visibility = musicVisibility;
        DurationControls.Visibility = SelectedStopRule == MusicStopRule.Duration ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Content = "开始";
        StopButton.Content = "全员停止";
        LocalStopButton.Content = emote ? "取消本机任务" : "本机停止";
        FlowHint.Text = emote ? "各成员准备好动作后，由房主统一开始。"
            : "各组乐谱使用者使用自己的乐谱同步开始，组员依序在游戏中加入。";
        ReadyButton.Visibility = IsRoomHost ? Visibility.Collapsed : Visibility.Visible;
        StartButton.IsEnabled &= !active;
        LeaderBox.IsEnabled &= !active;
        var phase = active ? performance!.Value.GetProperty("phase").GetString() : null;
        var beforeStart = active && (_roomClock.ServerNowMs is not { } now || now < (performance!.Value.TryGetProperty("latestStartServerMs", out var latest) ? latest.GetDouble() : performance.Value.GetProperty("startAction").GetProperty("targetServerMs").GetDouble()));
        var host = ViewSession is { } identity && ViewRoom?.GetProperty("hostId").GetString() == identity.MemberId;
        CountdownBox.IsEnabled = !active && host && ViewConnected;
        CountdownHint.Text = host && !active ? _countdownError ?? "" :
            active && performance!.Value.TryGetProperty("countdownMs", out var ms) ?
                $"本轮倒计时 {ms.GetInt32() / 1000.0:0.###} 秒。" +
                (performance.Value.TryGetProperty("requestedCountdownMs",out var requested) && requested.GetInt32()<ms.GetInt32()?" 已自动延长倒计时，为提前触发和网络传输留出时间。":"") : "";
        CountdownBox.ToolTip = _countdownError ?? "1–60 秒，支持小数；开始时自动激活游戏窗口。";
        StartButton.IsEnabled &= _countdownError is null;
        StartButton.IsEnabled &= _rosterEdits==0;
        StartButton.IsEnabled &= emote || SelectedStopRule != MusicStopRule.Duration || _musicDurationError is null;
        CancelButton.IsEnabled &= beforeStart;
        StopButton.IsEnabled &= active;
        var pendingStart = active ? _scheduler.LocalDeadline(performance!.Value.GetProperty("startAction").GetProperty("id").GetString()!) : null;
        LocalStopButton.IsEnabled = active && (!emote || pendingStart is not null);
        RefreshRoleLayout(active, beforeStart);
    }
    private void RefreshStatus() => WithRoomView(RefreshStatusView);
    private void RefreshStatusView()
    {
        ConnectionStatus.Text = ViewConnected ? "已连接 MabiSync" : "尚未连接";
        ClockText.Text = _roomClock.Estimate is { } estimate ? $"网络往返 {estimate.RoundTripMs:F1} ms · 校时估计误差 ±{estimate.UncertaintyMs:F1} ms" : "正在校时";
        var p = ViewRoom?.GetProperty("performance");
        RefreshMusicStatus(p);
        RefreshNextStep();
        RefreshCountdown(p);RefreshFeedback();
        PhaseText.Foreground=ColorBrush("#9DC7FF");
        if(ViewSession is not null && !ViewConnected){PhaseText.Text="房间连接已中断";PhaseText.Foreground=ColorBrush("#FFAFB9");return;}
        if (p is not { ValueKind: JsonValueKind.Object } || p.Value.GetProperty("phase").GetString() is "cancelled" or "stopped") {
            PhaseText.Text = ViewSession is null ? "连接房间" : IdleReadinessText();
            if(ViewSession is not null)PhaseText.Foreground=ColorBrush(PhaseText.Text is "已就绪" or "全员已就绪"?"#86E1B2":IsRoomHost?"#9DC7FF":"#FFAFB9");
            return;
        }
        var value = p.Value; var phase = value.GetProperty("phase").GetString();
        var emote = value.GetProperty("mode").GetString() == "emote";
        var prefix = _inputRouter.UseSimulation ? "模拟" : "";
        var now = _roomClock.ServerNowMs;
        var start = value.GetProperty("startAction");
        var target = start.GetProperty("targetServerMs").GetDouble() + TimingOffsetMs;
        var localReceipt = _receipts.LastOrDefault(r => r.ActionId == start.GetProperty("id").GetString());
        PhaseText.Text = phase switch
        {
            "preparing" => "正在准备",
            "committed" or "performing" when IsManualEnsembleJoiner => "等待组内加入",
            "committed" when localReceipt is not null => localReceipt.Status switch { "executed" => "本机操作已执行", "failed" => "本机操作失败", "missed" => "已错过执行时刻", _ => "本机操作已结束，请查看执行记录" },
            "committed" when now is not null => $"本机{prefix}{(emote ? "表情" : "演奏")}将在 {Math.Max(0, ((_scheduler.LocalDeadline(start.GetProperty("id").GetString()!) is { } deadline ? deadline - _clock.NowMs : target - now.Value)) / 1000):F1} 秒后触发",
            "committed" => "任务已安排",
            "performing" => "本轮进行中",
            "interrupted" => "演奏中断",
            "cancelled" => "本轮已取消",
            "stopped" when emote => "本轮已结束",
            "stopped" => "本轮已结束",
            _ => "等待准备"
        };
        if(phase=="interrupted" || localReceipt?.Status is "failed" or "missed")PhaseText.Foreground=ColorBrush("#FFAFB9");
        else if(phase=="stopped")PhaseText.Foreground=ColorBrush("#86E1B2");
    }
    private string IdleReadinessText()
    {
        var self=_members.FirstOrDefault(member=>member.Id==ViewSession?.MemberId);
        return IsRoomHost ? _members.Any(member=>member.Selected && member.Id!=self?.Id && (!member.Ready || member.Status=="已离线"))?"等待全员就绪":"全员已就绪"
            : self?.Ready==true?"已就绪":"未就绪";
    }
    private async Task Run(Button? button, Func<Task> operation)
    {
        if (_closing) return;
        if (button is not null) {button.IsEnabled = false;Feedback("正在处理…",FeedbackTone.Info);}
        try { await operation();if(button is not null && MessageText.Text=="正在处理…")Feedback("请求已发送，房间状态将自动更新。",FeedbackTone.Info); }
        catch (Exception error) { Feedback(Friendly(error.Message),FeedbackTone.Error); }
        finally { if (button is not null) button.IsEnabled = true; if (!_closing) Refresh(); }
    }
    private async void ConnectClick(object sender, RoutedEventArgs e) => await RunConnection((Button)sender, async () => {if(_client.Session is not null){await ReconnectRoom();return;}await _client.ConnectAsync(EndpointBox.Text.Trim());Feedback("已连接，请创建或加入房间。");SaveSettings();});
    private async void StartServerClick(object sender, RoutedEventArgs e) => await RunConnection((Button)sender, async () => { RequireNoRoom(); await _server.StartAsync(LanCheck.IsChecked == true); EndpointBox.Text = "ws://127.0.0.1:3132"; await _client.ConnectAsync(EndpointBox.Text); MessageText.Text = _server.Status; });
    private async void CreateClick(object sender, RoutedEventArgs e) => await RunConnection((Button)sender, async () => { await CreateConfiguredRoom(RoomNickname()); SaveSettings(); });
    private async void JoinClick(object sender, RoutedEventArgs e) => await RunConnection((Button)sender, async () => { await _client.JoinRoomAsync(RoomNickname(), CodeBox.Text.Trim().ToUpperInvariant()); SaveSettings(); });
    private async void LeaveClick(object sender, RoutedEventArgs e)
    {
        if(_leavingRoom || _closing)return;
        _leavingRoom=true;_scheduler.RoomExited();
        try {
            if(_connectionBusy) {
                if(_networkOperation is null)return;
                var pending=_connectionFinished;_networkOperation.Cancel();RefreshConnection();
                if(pending is not null)await pending;
            }
            await RunConnection((Button)sender,CloseOrLeaveRoom);
        }catch(Exception error){Feedback(Friendly(error.Message),FeedbackTone.Error);}
        finally{_leavingRoom=false;if(!_closing)Refresh();}
    }
    private async void OpenPublicClick(object sender, RoutedEventArgs e) => await RunConnection((Button)sender, async () =>
    {
        if(_usingCommunity || SelectedProvider=="community")throw new InvalidOperationException("服务器直连已包含公网连接，请创建或加入房间。");
        if (ViewSession is { } identity && (ViewRoom?.GetProperty("hostId").GetString() != identity.MemberId || new Uri(_client.Endpoint).Host is not ("127.0.0.1" or "localhost")))
            throw new InvalidOperationException("请由本机房间的房主建立公网连接。加入他人房间时直接使用对方的服务地址。");
        await _server.StartAsync(LanCheck.IsChecked == true);
        if (!ViewConnected || ViewSession is null) { EndpointBox.Text = "ws://127.0.0.1:3132"; await _client.ConnectAsync(EndpointBox.Text); }
        await EstablishPublic();
    });
    private async void ClosePublicClick(object sender, RoutedEventArgs e) => await RunConnection((Button)sender, async () => {if(_usingCommunity || _usingLan){await CloseOrLeaveRoom();return;}if(ViewSession is not null && !IsRoomHost)throw new InvalidOperationException("离开房间后将自动关闭本机连接。");await StopNetworks();Feedback("公网连接已关闭，房间仍保留。",FeedbackTone.Warning);});
    private async void CopyPublicClick(object sender,RoutedEventArgs e)=>await Run((Button)sender,CopyInvitationAsync);
    private async void ReadyClick(object sender, RoutedEventArgs e) => await Run((Button)sender, async () =>
    {
        var self = ViewRoom!.Value.GetProperty("members").EnumerateArray().First(m => m.GetProperty("id").GetString() == ViewSession!.MemberId);
        var ready = !self.GetProperty("ready").GetBoolean();
        if (ready) { await _client.RefreshClockAsync(); ValidateLocalReadiness(); _inputRouter.ReadyMode = SelectedMode; _inputRouter.StartsEnabled = !IsManualEnsembleJoiner; }
        else InvalidateReadiness();
        await _client.SendAsync(new { type = "set-ready", ready, capabilities = LocalCapabilities() });
    });
    private async void LeaderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || ViewSession is null || LeaderBox.SelectedValue is not string id) return;
        await Run(null, () => _client.SendAsync(new { type = "select-leader", memberId = id }));
    }
    private async void PrepareClick(object sender, RoutedEventArgs e) => await Run((Button)sender, async () =>
    {
        var mode = (string)((ComboBoxItem)ModeBox.SelectedItem).Tag;
        var countdownMs = CountdownText.ParseMilliseconds(CountdownBox.Text);
        var participants = _members.Where(m => m.Selected).Select(m => m.Id).ToArray();
        if(_rosterEdits>0)throw new InvalidOperationException("正在同步成员列表，请稍候。");
        if(participants.Length==0)throw new InvalidOperationException("请先选择本轮参与者。");
        var waiting=_members.Where(m=>m.Selected && m.Id!=ViewSession?.MemberId && !m.Ready).Select(m=>m.Nickname).ToArray();
        if(waiting.Length>0)throw new InvalidOperationException("等待就绪："+string.Join("、",waiting)+"。");
        MessageText.Text="正在同步成员时钟……";
        await _client.RefreshClockAsync();
        if(!_inputRouter.UseSimulation && participants.Contains(ViewSession?.MemberId))await _gamePlatform.ActivateAsync(SelectedWindow??throw new InvalidOperationException("请先选择游戏窗口。"),_calibrationCancellation.Token);
        if (participants.Contains(ViewSession?.MemberId))
        {
            ValidateLocalReadiness(); _inputRouter.ReadyMode = mode; _inputRouter.StartsEnabled = !IsManualEnsembleJoiner;
            await _client.SendAsync(new { type = "configure-input", capabilities = LocalCapabilities() });
        }
        if (mode != "emote" && SelectedStopRule == MusicStopRule.Duration)
        {
            var stopAfterMs = MusicDurationText.ParseMilliseconds(DurationBox.Text);
            await _client.SendAsync(new { type = "prepare", mode, countdownMs, participantIds = participants, stopAfterMs });
        }
        else if (mode != "emote" && SelectedStopRule == MusicStopRule.FirstCycle)
        {
            if (!_client.SupportsFirstCycleStop) throw new InvalidOperationException("当前服务器版本不支持一轮停止，请房主更新服务器后重新建房。");
            await _client.SendAsync(new { type = "prepare", mode, countdownMs, participantIds = participants, stopRule = "first-cycle" });
            MessageText.Text = "等待识别总指挥本轮的乐谱，识别后会安排全员停止。";
        }
        else
        {
            await _client.SendAsync(new { type = "prepare", mode, countdownMs, participantIds = participants });
            if (mode != "emote") MessageText.Text = "各组乐谱使用者将同步开始，组员请在游戏中依序加入，由房主统一停止。";
        }
        SaveSettings();
    });
    private async void CancelClick(object sender, RoutedEventArgs e) => await Run((Button)sender, () => _client.SendAsync(new { type = "cancel" }));
    private async void StopClick(object sender, RoutedEventArgs e) => await Run((Button)sender, () => _client.SendAsync(new { type = "stop-all" }));
    private async void ContinueClick(object sender, RoutedEventArgs e) => await Run((Button)sender, () => _client.SendAsync(new { type = "decide", decision = "continue" }));
    private async void LocalStopClick(object sender, RoutedEventArgs e) => await Run((Button)sender, () => { StopLocally(); return Task.CompletedTask; });
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "JSON 诊断|*.json", FileName = "mabisync-diagnostics.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new { product = "MabiSync", version = typeof(App).Assembly.GetName().Version?.ToString(), simulatedOnly = _receipts.All(r => r.Simulated), os = Environment.OSVersion.VersionString, clock = _roomClock.Estimate, faults = AppFaultLog.ReadRecent(), receipts = _receipts.Select(r => new { r.Status, r.Simulated, r.GameResponseVerified, r.AppliedTimingOffsetMs, deviationMs = r.ActualLocalMs - r.PlannedLocalMs }) }, new JsonSerializerOptions { WriteIndented = true })); MessageText.Text = "已导出诊断。"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { MessageText.Text = error.Message; }
    }
    private void LoadSettings()
    {
        if (!File.Exists(_settings)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_settings)); var root = doc.RootElement;
            EndpointBox.Text = root.GetProperty("endpoint").GetString() ?? EndpointBox.Text;
            NicknameBox.Text = root.GetProperty("nickname").GetString() ?? NicknameBox.Text;
            if(root.TryGetProperty("hideFromCapture",out var captureHidden) && captureHidden.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _hideFromCapture=captureHidden.GetBoolean();
            if(root.TryGetProperty("publicProvider",out var provider))PublicProviderBox.SelectedIndex=provider.GetString() switch {"lan"=>1,"cloudflare"=>0,_=>2};
            if(root.TryGetProperty("lanEndpoint",out var lan) && lan.ValueKind==JsonValueKind.String) LanAddressBox.Text=lan.GetString()??LanAddressBox.Text;
            if (root.TryGetProperty("timingOffsetMs", out var adjustment) && adjustment.TryGetInt32(out var offset) && offset is >= -2000 and <= 2000) TimingOffsetSlider.Value = offset;
            if (root.TryGetProperty("countdownMs", out var countdown) && countdown.TryGetInt32(out var milliseconds) && milliseconds is >= 1000 and <= 60000)
            { _countdownMs = milliseconds; CountdownBox.Text = (milliseconds / 1000m).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); }
            if (root.TryGetProperty("musicDurationMs", out var duration) && duration.TryGetInt64(out var durationMs) && durationMs is >= 1 and <= 86400000)
            { _musicDurationMs = durationMs; DurationBox.Text = (durationMs / 1000m).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); }
            var stopRule = root.TryGetProperty("musicStopRule", out var rule) && rule.ValueKind == JsonValueKind.String ? rule.GetString() : null;
            int? legacyChoice = root.TryGetProperty("musicStopChoice", out var stopChoice) && stopChoice.TryGetInt32(out var choice) ? choice : null;
            StopChoice.SelectedIndex = MusicStopSettings.Restore(stopRule, legacyChoice) switch { MusicStopRule.Duration => 1, MusicStopRule.FirstCycle => 2, _ => 0 };
            if (root.TryGetProperty("bindings", out var bindings)) LoadInputSettings(bindings);
            if(root.TryGetProperty("startInputKinds",out var kinds) && kinds.ValueKind==JsonValueKind.Object)
                foreach(var mode in new[]{"emote","independent","ensemble"})
                    if(kinds.TryGetProperty(mode,out var kind) && kind.TryGetInt32(out var index) && index>=0 && index<=(mode=="emote"?2:1))_startInputKinds[mode]=index;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            try { File.Copy(_settings, _settings + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), false); } catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException) { }
            MessageText.Text = "无法读取已保存的设置，已恢复默认设置。";
        }
    }
    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settings)!);
            var temp = _settings + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new { endpoint = EndpointBox.Text.Trim(), publicProvider=SelectedProvider, lanEndpoint=LanAddressBox.Text.Trim(), nickname = NicknameBox.Text.Trim(), hideFromCapture = _hideFromCapture, countdownMs = _countdownMs, timingOffsetMs = TimingOffsetMs, musicDurationMs = _musicDurationMs, musicStopRule = MusicStopSettings.Persist(SelectedStopRule), musicStopChoice = SelectedStopRule == MusicStopRule.Duration ? 2 : 1, bindings = _modeBindings, startInputKinds = _startInputKinds }));
            File.Move(temp, _settings, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { MessageText.Text = "设置未能保存：" + error.Message; }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true; if (_closing) return;
        _closing = true; IsEnabled = false; _displayTimer.Stop(); _observationTimer.Stop(); _calibrationCancellation.Cancel(); _hotkey?.Dispose(); SaveSettings();
        _scheduler.CancelAll();
        _networkOperation?.Cancel();
        try
        {
            if(_client.Connected && _client.Session is not null)await Cleanup("close-room",()=>_client.LeaveRoomAsync(IsRoomHost));
            await Cleanup("close-capture", () => _gameObservation.DisposeAsync().AsTask());
            await Cleanup("close-tunnel", () => _tunnel.DisposeAsync().AsTask());
            await Cleanup("close-client", () => _client.DisposeAsync().AsTask());
            await Cleanup("close-observation-send", () => _observationSend);
        }
        finally
        {
            try { _scheduler.Dispose(); } catch (Exception error) { AppFaultLog.Record(error, "close-scheduler"); }
            try { _server.Dispose(); } catch (Exception error) { AppFaultLog.Record(error, "close-service"); }
            _closed = true; _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        async Task Cleanup(string stage, Func<Task> cleanup)
        { try { await cleanup(); } catch (Exception error) { AppFaultLog.Record(error, stage); } }
    }
    private static string ActionStatusText(string status) => status switch
    {
        "executed" => "已执行", "failed" => "执行失败", "missed" => "未能按时执行", "cancelled" => "已取消", _ => status
    };
    private static string Friendly(string message) => message switch
    {
        "BAD_NICKNAME" => "请填写 1–20 字的昵称，或通过抓包识别更新昵称。",
        "MEMBER_NOT_READY" => "选中的成员尚未全部就绪，请让成员就绪或取消勾选。",
        "NO_ENSEMBLE_INITIATOR" => "请房主在成员列表中指定至少一位“乐谱使用者”，并勾选其参与本轮。",
        "CANNOT_KICK_HOST" => "退出请点击“关闭房间”，所有成员会一起退出。",
        "BAD_ENSEMBLE_GROUP" => "合奏组编号应为 1–24。",
        "BAD_ENSEMBLE_ORDER" => "顺序应为 1–24，组内按连续顺序排列。",
        "BAD_INSTRUMENT" => "乐器名称最多 40 字，请去掉换行和控制字符。",
        "CHARACTER_NOT_OBSERVED" => "抓包暂未识别到这个角色，请等待合奏成员列表更新后重试。",
        "CAPTURE_IDENTITY_CONFLICT" => "已通过该成员的抓包识别其角色，无需手动匹配。",
        "BAD_OBSERVATION" => "加入顺序未更新：抓包数据未通过校验。",
        "OBSERVATION_RECONNECT_REQUIRED" => "角色识别信息已过期，请点击“重新连接”后重试。",
        "FORBIDDEN" => "该操作由房主执行。",
        "SESSION_ACTIVE" => "本轮任务仍在进行，请先取消或统一停止。",
        "USE_STOP_ALL" => "开始时刻已经过去，请使用全员停止。",
        "LEADER_NOT_PARTICIPATING" => "请将指定总指挥加入本轮参与者。",
        "AUTOMATIC_END_UNVERIFIED" => "暂时无法自动判断一轮结束，请选择“手动指定演奏时长”或“房主手动统一停止”。",
        "STOP_NOT_APPLICABLE" => "表情动作只需同步开始，无需设置停止。",
        "NOT_IN_ROOM" => "请先创建或加入房间。",
        "COUNTDOWN_BEFORE_LOCAL_TRIGGER" => "倒计时短于某位成员的提前量，请增加倒计时或减小该成员的提前量。",
        "ALREADY_IN_ROOM" => "请先离开当前房间。",
        "ROOM_NOT_FOUND" or "SESSION_EXPIRED" => "房间或身份已失效，请重新创建或加入。",
        "ROOM_FULL" => "房间已满，请房主移出离线成员，或加入其他房间。",
        "SERVER_BUSY" => "服务器繁忙，请稍后重试。",
        "RATE_LIMIT" => "操作太快，请稍等几秒再试。",
        "INCOMPATIBLE_CLIENT" => "工具与服务器版本不兼容，请更新后重新连接。",
        "MEMBER_NOT_FOUND" => "该成员已离开房间，请刷新后重试。",
        "BAD_PARTICIPANTS" => "本轮成员列表有变化，请重新选择参与者。",
        "BAD_COUNTDOWN" => "倒计时请填写 1–60 秒。",
        "BAD_DURATION" => "演奏时长请填写 0.001–86400 秒。",
        "BAD_STOP_RULE" => "无法识别停止方式，请重新选择。",
        "NO_INTERRUPTION" => "当前没有需要处理的演奏中断。",
        "STALE_ACTION" or "STALE_SESSION" => "本轮操作已失效，请确认房间状态后重试。",
        "BAD_MESSAGE" or "BAD_CAPABILITIES" or "BAD_RECEIPT" => "服务器无法识别本次操作，请更新工具后重新连接。",
        _ => message
    };
}
