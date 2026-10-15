namespace MabiSync.Core;

public sealed record GameWindowId(nint Handle, int ProcessId, long ProcessStartedUtcTicks, string ImagePath);
public sealed record ClientArea(int Left, int Top, int Width, int Height, uint Dpi);
public sealed record GameWindowState(GameWindowId Identity, ClientArea Client, bool Visible, bool Foreground, bool InputPermission);
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
            lock (_gate) if (!_executing && (_downKeys.Count > 0 || _mouseDown)) throw new InvalidOperationException("上次输入尚未释放，请先使用本机停止重试释放。");
            var p = _profile() ?? throw new InvalidOperationException("尚未选择游戏窗口与本机绑定。");
            var bindings = action.Mode switch { "emote" => p.Emote, "independent" => p.Independent, "ensemble" => p.Ensemble, _ => throw new InvalidOperationException("未知动作模式。") };
            if (action.Kind is not ("start" or "stop") || (action.Mode == "emote" && action.Kind == "stop")) throw new InvalidOperationException("表情流程不包含停止操作。");
            var toggle = bindings.StopIsToggle || (bindings.Start is not null && bindings.Start == bindings.Stop);
            if (action.Kind == "start" && action.Mode != "emote" && (bindings.Stop is null || (toggle && !_evidence.Available)))
                throw new InvalidOperationException("当前没有可靠的演奏停止路径，请设置专用停止绑定或先使用模拟模式。");
            var binding = (action.Kind == "start" ? bindings.Start : bindings.Stop) ?? throw new InvalidOperationException("本机尚未设置对应动作绑定。");
            if (binding is ChatDraftBinding && (action.Mode != "emote" || action.Kind != "start")) throw new InvalidOperationException("预填指令回车仅用于表情触发。");
            if (binding is ChatDraftBinding && Volatile.Read(ref _chatDraftConfirmed) == 0) throw new InvalidOperationException("请在游戏聊天框预填指令，再重新点击本机就绪；每轮只发送一次回车。");
            var state = Inspect(p.Window, false);
            ValidateBinding(binding, state.Client);
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
            if (_executing) return new("failed", null, false, "另一个动作正在执行。");
            if (!_prepared.Remove(action.Id, out p!)) return new("failed", null, false, "动作未准备或已处理。");
            _executing = true;
        }
        double? sent = null; InputOutcome result;
        try
        {
            if (p.ChatDraft && Interlocked.Exchange(ref _chatDraftConfirmed, 0) == 0) throw new InvalidOperationException("预填指令尚未重新确认，本次未发送回车。");
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) if (_downKeys.Count > 0 || _mouseDown) throw new InvalidOperationException("上次输入尚未释放，本次未发送新输入。");
            var state = Inspect(p.Window, true);
            ValidateBinding(p.Binding, state.Client);
            if (p.Toggle)
            {
                var observation = _evidence.Read(action.SessionId);
                if (observation is null || observation.SessionId != action.SessionId || !SameWindow(observation.Window, p.Window) ||
                    !observation.Playing || !double.IsFinite(observation.ValidUntilLocalMs) || observation.ValidUntilLocalMs < _clock.NowMs)
                    return new("cancelled", null, false, "无法确认本轮仍在演奏，未发送开停切换键，避免重新开始。");
            }
            if (p.Binding is KeyBinding key)
            {
                var chord = Chord(key).ToArray();
                if (UnexpectedModifierHeld(key)) throw new InvalidOperationException("请松开额外的 Ctrl、Alt、Shift 或 Windows 键后重试。");
                if (chord.Any(_platform.KeyIsDown)) throw new InvalidOperationException("绑定按键当前被按住，请松开后重新准备。");
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
                if (_platform.LeftButtonIsDown()) throw new InvalidOperationException("鼠标左键当前被按住。");
                var x = checked(state.Client.Left + button.X); var y = checked(state.Client.Top + button.Y);
                if (!_platform.PointBelongsToWindow(p.Window, x, y)) throw new InvalidOperationException("操作位置被其他窗口遮挡。");
                _platform.MovePointer(x, y);
                var latest = Inspect(p.Window, true);
                if (latest.Client != state.Client || !_platform.PointBelongsToWindow(p.Window, x, y)) throw new InvalidOperationException("发送点击前窗口位置或遮挡发生变化。");
                lock (_gate) { cancellationToken.ThrowIfCancellationRequested(); _mouseDown = true; _platform.LeftButton(true); sent = _clock.NowMs; }
            }
            if (cancellationToken.WaitHandle.WaitOne(40)) cancellationToken.ThrowIfCancellationRequested();
            result = new("executed", sent, false, "普通前台输入已发送；游戏响应尚未确认。");
        }
        catch (OperationCanceledException) { result = new("cancelled", sent, false, sent is null ? "发送前取消，已释放本工具按键。" : "动作输入可能已生效；已取消后续操作并释放按键。"); }
        catch (Exception error) { result = new("failed", sent, false, error.Message); }
        finally { lock (_gate) { ReleaseAll(); _executing = false; } }
        lock (_gate) if (_downKeys.Count > 0 || _mouseDown) return new("failed", sent, false, "系统拒绝释放输入；请手动松开按键并检查输入权限。");
        return result;
    }
    private GameWindowState Inspect(GameWindowId window, bool foreground)
    {
        var state = _platform.Inspect(window);
        if (state is null || !SameWindow(state.Identity, window)) throw new InvalidOperationException("游戏窗口或进程已改变，请重新选择。");
        if (!state.Visible || state.Client.Width <= 0 || state.Client.Height <= 0) throw new InvalidOperationException("请恢复游戏窗口，不能向最小化窗口发送输入。");
        if (!state.InputPermission) throw new InvalidOperationException("工具与游戏输入权限不匹配；请以相同权限运行。");
        if (foreground && !state.Foreground) throw new InvalidOperationException("游戏不在前台，本次未发送动作。");
        return state;
    }
    private static bool SameWindow(GameWindowId a, GameWindowId b) => a.Handle == b.Handle && a.ProcessId == b.ProcessId &&
        a.ProcessStartedUtcTicks == b.ProcessStartedUtcTicks && string.Equals(a.ImagePath, b.ImagePath, StringComparison.OrdinalIgnoreCase);
    private static void ValidateBinding(GameBinding binding, ClientArea area)
    {
        if (binding is ChatDraftBinding) return;
        if (binding is KeyBinding k)
        {
            if (k.Key is < 0x08 or > 0xfe or 0x10 or 0x11 or 0x12 or 0x5b or 0x5c or 0x78) throw new InvalidOperationException("请设置有效快捷键；F9保留为本机停止键。");
        }
        else if (binding is ButtonBinding b)
        {
            if (b.X < 0 || b.Y < 0 || b.X >= b.ClientWidth || b.Y >= b.ClientHeight || b.ClientWidth != area.Width || b.ClientHeight != area.Height || b.Dpi != area.Dpi)
                throw new InvalidOperationException("按钮校准已失效，请按当前游戏客户区尺寸和DPI重新校准。");
        }
        else throw new InvalidOperationException("未知本机绑定类型。");
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
