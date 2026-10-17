namespace MabiSync.Core;

public sealed record GameWindowId(nint Handle, int ProcessId, long ProcessStartedUtcTicks, string ImagePath);
public sealed record ClientArea(int Left, int Top, int Width, int Height, uint Dpi);
public sealed record GameWindowState(GameWindowId Identity, ClientArea Client, bool Visible, bool Foreground, bool InputPermission)
{
    public string Title {get;init;}="";
    public bool Minimized {get;init;}
    public string Label=>$"{(string.IsNullOrWhiteSpace(Title)?Path.GetFileName(Identity.ImagePath):Title)} · {Path.GetFileName(Identity.ImagePath)} · PID {Identity.ProcessId} · "+
        (Minimized?"已最小化":$"{Client.Width}×{Client.Height} · DPI {Client.Dpi}");
}
[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(KeyBinding), "key")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ButtonBinding), "button")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ChatDraftBinding), "chat-draft")]
public abstract record GameBinding;
/// <summary>Player opens chat and prepares the draft. Dispatch submits one Enter; no text is read or written.</summary>
public sealed record ChatDraftBinding : GameBinding;
public sealed record KeyBinding(ushort Key, bool Control = false, bool Alt = false, bool Shift = false) : GameBinding;
public sealed record ButtonBinding(int X, int Y, int ClientWidth, int ClientHeight, uint Dpi) : GameBinding;
public sealed record ModeBindings(GameBinding? Start = null, GameBinding? Stop = null, bool StopIsToggle = false);
public sealed record GameInputProfile(GameWindowId Window, ModeBindings Emote, ModeBindings Independent, ModeBindings Ensemble);
public sealed record PlaybackObservation(GameWindowId Window, string SessionId, bool Playing, double ValidUntilLocalMs);

public interface IPlaybackEvidence { bool Available { get; } PlaybackObservation? Read(string sessionId); }
public sealed class UnknownPlaybackEvidence : IPlaybackEvidence { public bool Available => false; public PlaybackObservation? Read(string sessionId) => null; }
public interface IGameInputPlatform
{
    GameWindowState? Inspect(GameWindowId window);
    bool KeyIsDown(ushort key);
    bool LeftButtonIsDown();
    bool PointBelongsToWindow(GameWindowId window, int screenX, int screenY);
    void Key(ushort key, bool down);
    void MovePointer(int screenX, int screenY);
    void LeftButton(bool down);
}

/// <summary>Uses configured local bindings only; the room never specifies a key or pointer position.</summary>
public sealed class GameInputExecutor : IInputExecutor
{
    private sealed record Prepared(GameWindowId Window, GameBinding Binding, bool Toggle, bool ChatDraft);
    private readonly IMonotonicClock _clock;
    private readonly IGameInputPlatform _platform;
    private readonly Func<GameInputProfile?> _profile;
    private readonly IPlaybackEvidence _evidence;
    private readonly object _gate = new();
    private readonly Dictionary<string, Prepared> _prepared = [];
    private readonly List<ushort> _downKeys = [];
    private bool _mouseDown;
    private bool _executing;
    private int _chatDraftConfirmed;
    public void ConfirmChatDraft() => Interlocked.Exchange(ref _chatDraftConfirmed, 1);
    public void RevokeChatDraft() => Interlocked.Exchange(ref _chatDraftConfirmed, 0);
    public GameInputExecutor(IMonotonicClock clock, IGameInputPlatform platform, Func<GameInputProfile?> profile, IPlaybackEvidence evidence)
    { _clock = clock; _platform = platform; _profile = profile; _evidence = evidence; }
    public bool IsSimulation(ScheduledAction action) => false;
    public InputPreparation Prepare(ScheduledAction action)
    {
        try
        {
            // Preparing is inert. Keys owned by the active dispatch are still being
            // released normally; only keys left over after it finishes block readiness.
            lock (_gate) if (!_executing && (_downKeys.Count > 0 || _mouseDown)) throw new InvalidOperationException("上次操作的按键或鼠标未能松开，请点击“本机停止”重试。");
            var p = _profile() ?? throw new InvalidOperationException("请先选择游戏窗口，并设置按键或按钮。");
            var bindings = action.Mode switch { "emote" => p.Emote, "independent" => p.Independent, "ensemble" => p.Ensemble, _ => throw new InvalidOperationException("无法识别本轮模式，请重新选择表情或合奏。") };
            if (action.Kind is not ("start" or "stop") || (action.Mode == "emote" && action.Kind == "stop")) throw new InvalidOperationException("表情动作无需停止。");
            var toggle = bindings.StopIsToggle || (bindings.Start is not null && bindings.Start == bindings.Stop);
            if (action.Kind == "start" && action.Mode != "emote" && (bindings.Stop is null || (toggle && !_evidence.Available)))
                throw new InvalidOperationException("请先设置停止按键或按钮。开始和停止需要分别设置。");
            var binding = (action.Kind == "start" ? bindings.Start : bindings.Stop) ?? throw new InvalidOperationException("请先设置此操作使用的按键或按钮。");
            if (binding is ChatDraftBinding && (action.Mode != "emote" || action.Kind != "start")) throw new InvalidOperationException("预填聊天指令只适用于表情模式。");
            if (binding is ChatDraftBinding && Volatile.Read(ref _chatDraftConfirmed) == 0) throw new InvalidOperationException("请在游戏聊天框填好指令，再点击“就绪”。每轮开始时自动按一次回车。");
            var state = Inspect(p.Window, false);
            // Minimized client geometry is not usable. Readiness validates the saved
            // binding itself; restored geometry is checked again before dispatch.
            ValidateBinding(binding,!state.Visible && binding is ButtonBinding b?new(0,0,b.ClientWidth,b.ClientHeight,b.Dpi):state.Client);
            lock (_gate) _prepared[action.Id] = new(p.Window, binding is ChatDraftBinding ? new KeyBinding(13) : binding, action.Kind == "stop" && toggle, binding is ChatDraftBinding);
            return new(true, null);
        }
        catch (Exception error) { return new(false, error.Message); }
    }
    public InputOutcome Execute(ScheduledAction action, CancellationToken cancellationToken)
    {
        Prepared p;
        lock (_gate)
        {
            if (_executing) return new("failed", null, false, "上一个操作还未完成，请稍后重试。");
            if (!_prepared.Remove(action.Id, out p!)) return new("failed", null, false, "本次操作尚未准备好，或已经执行过。请重新就绪后再开始。");
            _executing = true;
        }
        double? sent = null; InputOutcome result;
        try
        {
            if (p.ChatDraft && Interlocked.Exchange(ref _chatDraftConfirmed, 0) == 0) throw new InvalidOperationException("本次未按回车。请重新填好指令并点击“就绪”。");
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) if (_downKeys.Count > 0 || _mouseDown) throw new InvalidOperationException("上次操作的按键或鼠标未能松开，本次操作已暂停。请点击“本机停止”重试。");
            var state = Inspect(p.Window, true);
            ValidateBinding(p.Binding, state.Client);
            if (p.Toggle)
            {
                var observation = _evidence.Read(action.SessionId);
                if (observation is null || observation.SessionId != action.SessionId || !SameWindow(observation.Window, p.Window) ||
                    !observation.Playing || !double.IsFinite(observation.ValidUntilLocalMs) || observation.ValidUntilLocalMs < _clock.NowMs)
                    return new("cancelled", null, false, "无法确认是否仍在演奏，已跳过切换按键。请在游戏中手动停止。");
            }
            if (p.Binding is KeyBinding key)
            {
                var chord = Chord(key).ToArray();
                if (UnexpectedModifierHeld(key)) throw new InvalidOperationException("请松开额外的 Ctrl、Alt、Shift 或 Windows 键后重试。");
                if (chord.Any(_platform.KeyIsDown)) throw new InvalidOperationException("请松开设置的按键，再重新就绪。");
                foreach (var k in chord)
                {
                    cancellationToken.ThrowIfCancellationRequested(); Inspect(p.Window, true);
                    lock (_gate)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        _downKeys.Add(k); _platform.Key(k, true);
                        if (k == key.Key) sent = _clock.NowMs;
                    }
                }
            }
            else if (p.Binding is ButtonBinding button)
            {
                if (_platform.LeftButtonIsDown()) throw new InvalidOperationException("请松开鼠标左键后重试。");
                var x = checked(state.Client.Left + button.X); var y = checked(state.Client.Top + button.Y);
                if (!_platform.PointBelongsToWindow(p.Window, x, y)) throw new InvalidOperationException("游戏按钮被其他窗口挡住，请移开遮挡后重试。");
                _platform.MovePointer(x, y);
                var latest = Inspect(p.Window, true);
                if (latest.Client != state.Client || !_platform.PointBelongsToWindow(p.Window, x, y)) throw new InvalidOperationException("游戏窗口刚刚移动或被遮挡，本次点击已取消。请重新就绪。");
                lock (_gate) { cancellationToken.ThrowIfCancellationRequested(); _mouseDown = true; _platform.LeftButton(true); sent = _clock.NowMs; }
            }
            if (cancellationToken.WaitHandle.WaitOne(40)) cancellationToken.ThrowIfCancellationRequested();
            result = new("executed", sent, false, "已发送按键或点击，请确认游戏中是否生效。");
        }
        catch (OperationCanceledException) { result = new("cancelled", sent, false, sent is null ? "操作已取消，按键已松开。" : "操作已取消，按键已松开。之前发送的操作可能已在游戏中生效。"); }
        catch (Exception error) { result = new("failed", sent, false, error.Message); }
        finally { lock (_gate) { ReleaseAll(); _executing = false; } }
        lock (_gate) if (_downKeys.Count > 0 || _mouseDown) return new("failed", sent, false, "系统未能松开按键或鼠标，请手动按下并松开，再检查工具与游戏的运行权限。");
        return result;
    }
    private GameWindowState Inspect(GameWindowId window, bool foreground)
    {
        var state = _platform.Inspect(window);
        if (state is null || !SameWindow(state.Identity, window)) throw new InvalidOperationException("游戏已关闭或重启，请重新选择游戏窗口。");
        if (foreground && (!state.Visible || state.Client.Width <= 0 || state.Client.Height <= 0)) throw new InvalidOperationException("游戏窗口未能从最小化恢复，本次操作已取消。请手动打开游戏窗口后重试。");
        if (!state.InputPermission) throw new InvalidOperationException("工具与游戏的运行权限不同，请让两者都以普通权限或管理员权限运行。");
        if (foreground && !state.Foreground) throw new InvalidOperationException("游戏未处于前台，本次操作已取消。请切回游戏后重试。");
        return state;
    }
    private static bool SameWindow(GameWindowId a, GameWindowId b) => a.Handle == b.Handle && a.ProcessId == b.ProcessId &&
        a.ProcessStartedUtcTicks == b.ProcessStartedUtcTicks && string.Equals(a.ImagePath, b.ImagePath, StringComparison.OrdinalIgnoreCase);
    private static void ValidateBinding(GameBinding binding, ClientArea area)
    {
        if (binding is ChatDraftBinding) return;
        if (binding is KeyBinding k)
        {
            if (k.Key is < 0x08 or > 0xfe or 0x10 or 0x11 or 0x12 or 0x5b or 0x5c or 0x78) throw new InvalidOperationException("请重新设置快捷键。F9 及其组合键用于本机停止，请换一个按键。");
        }
        else if (binding is ButtonBinding b)
        {
            if (b.X < 0 || b.Y < 0 || b.Dpi==0 || b.X >= b.ClientWidth || b.Y >= b.ClientHeight || b.ClientWidth != area.Width || b.ClientHeight != area.Height || b.Dpi != area.Dpi)
                throw new InvalidOperationException("游戏窗口大小或缩放已改变，请重新点选按钮位置。");
        }
        else throw new InvalidOperationException("无法读取按键或按钮设置，请重新设置。");
    }
    private static IEnumerable<ushort> Chord(KeyBinding key)
    {
        if (key.Control) yield return 0x11;
        if (key.Alt) yield return 0x12;
        if (key.Shift) yield return 0x10;
        yield return key.Key;
    }
    private bool UnexpectedModifierHeld(KeyBinding key) =>
        (!key.Control && new ushort[] { 0x11, 0xa2, 0xa3 }.Any(_platform.KeyIsDown)) ||
        (!key.Alt && new ushort[] { 0x12, 0xa4, 0xa5 }.Any(_platform.KeyIsDown)) ||
        (!key.Shift && new ushort[] { 0x10, 0xa0, 0xa1 }.Any(_platform.KeyIsDown)) ||
        _platform.KeyIsDown(0x5b) || _platform.KeyIsDown(0x5c);
    public void Forget(string actionId) { lock (_gate) _prepared.Remove(actionId); }
    public void ReleaseAll()
    {
        lock (_gate)
        {
            if (_mouseDown) try { _platform.LeftButton(false); _mouseDown = false; } catch (Exception) { /* retain ownership for the next release attempt */ }
            for (var i = _downKeys.Count - 1; i >= 0; i--)
                try { _platform.Key(_downKeys[i], false); _downKeys.RemoveAt(i); } catch (Exception) { /* retain ownership */ }
        }
    }
}
