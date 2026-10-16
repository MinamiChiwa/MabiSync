using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using MabiSync.Core;

namespace MabiSync.Client;

public sealed record SessionIdentity(string Code, string MemberId, string Token, string ServerEpoch, string Endpoint);

public sealed partial class RoomClient : IAsyncDisposable
{
    private sealed record Snapshot(JsonElement Value);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private readonly IMonotonicClock _clock;
    private readonly RoomClock _roomClock;
    private readonly ActionScheduler _scheduler;
    private readonly double _inactivityTimeoutMs;
    private double _lastInboundMs;
    private readonly SemaphoreSlim _send = new(1, 1), _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<int, double> _probes = new();
    private readonly ConcurrentDictionary<int,TaskCompletionSource<bool>> _freshProbes=new();
    private readonly ConcurrentDictionary<string, ScheduledAction> _prepared = new();
    private readonly ConcurrentDictionary<string, byte> _committed = new();
    private readonly Channel<ActionReceipt> _receipts = Channel.CreateUnbounded<ActionReceipt>(new() { SingleReader = true });
    private readonly Task _receiptPump;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _connection;
    private Task? _receiveTask, _syncTask;
    private TaskCompletionSource<bool>? _hello;
    private TaskCompletionSource<bool>? _roomResume;
    private SessionIdentity? _resumeIdentity;
    private bool _resumeIdentityConfirmed;
    private volatile bool _restoringRoom;
    private TaskCompletionSource<SessionIdentity>? _roomRequest;
    private TaskCompletionSource<bool>? _roomExit;
    private bool _closeRequested;
    private string? _expectedClosure;
    public int UnconfirmedRoomExits {get;private set;}
    private readonly ConcurrentDictionary<string,TaskCompletionSource<bool>> _requests=new();
    private volatile string? _epoch, _activeSession;
    private volatile string _activeMode = "emote";
    private Snapshot? _snapshot;
    private SessionIdentity? _session;
    private int _syncId;
    private volatile bool _connected, _disposed;
    public bool Connected => _connected && !_restoringRoom;
    public bool SupportsGameObservation { get; private set; }
    public bool SupportsFirstCycleStop { get; private set; }
    public bool SupportsRosterEditing { get; private set; }
    public string Endpoint { get; private set; } = "ws://127.0.0.1:3132/";
    public SessionIdentity? Session => Volatile.Read(ref _session);
    public JsonElement? Room => Volatile.Read(ref _snapshot)?.Value;
    public event Action? Changed;
    public event Action<string>? Notice;
    public event Action<SessionIdentity>? RoomRestored;
    public event Action<ActionReceipt>? ReceiptReported;
    public RoomClient(IMonotonicClock clock, RoomClock roomClock, ActionScheduler scheduler,TimeSpan? inactivityTimeout=null)
    {
        _clock = clock; _roomClock = roomClock; _scheduler = scheduler;
        _inactivityTimeoutMs=(inactivityTimeout??TimeSpan.FromSeconds(10)).TotalMilliseconds;
        _scheduler.Receipt += QueueReceipt;
        _receiptPump = Task.Run(ReceiptLoop);
        _preparationPump=Task.Run(PreparationLoop);
    }
    private void QueueReceipt(ActionReceipt value) => _receipts.Writer.TryWrite(value);
    public Task<SessionIdentity> CreateRoomAsync(string nickname, CancellationToken token = default) =>
        RequestRoomAsync(new { type = "create", nickname }, token);
    public Task<SessionIdentity> JoinRoomAsync(string nickname, string code, CancellationToken token = default) =>
        RequestRoomAsync(new { type = "join", nickname, code }, token);
    public async Task UpdateRosterAsync(string type,string memberId,int? group=null,int? order=null,string? musicRole=null,string? instrument=null,bool? selected=null)
    {
        if(!SupportsRosterEditing)throw new InvalidOperationException("房间服务需更新后才能直接编辑成员列表。");
        await ConfirmAsync(new{type,memberId,group,order,musicRole,instrument,selected}).ConfigureAwait(false);
    }
    public Task SetRoomModeAsync(string mode)=>ConfirmAsync(new{type="set-room-mode",mode});
    /// <summary>Collect a short burst before readiness/start; replies stay on the receive loop.</summary>
    public async Task RefreshClockAsync(CancellationToken token=default)
    {
        var requests=new List<(int Id,TaskCompletionSource<bool> Reply)>();
        using var limit=CancellationTokenSource.CreateLinkedTokenSource(token,_connection?.Token??CancellationToken.None);
        limit.CancelAfter(TimeSpan.FromSeconds(4));
        try {
            for(var i=0;i<6;i++) {
                var reply=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var id=Interlocked.Increment(ref _syncId);_freshProbes[id]=reply;requests.Add((id,reply));
                await SendProbeAsync(id,limit.Token).ConfigureAwait(false);
                if(i<5)await Task.Delay(40,limit.Token).ConfigureAwait(false);
            }
            await Task.WhenAll(requests.Select(r=>r.Reply.Task)).WaitAsync(limit.Token).ConfigureAwait(false);
        } catch(OperationCanceledException) when(!token.IsCancellationRequested && Connected && _roomClock.Estimate is not null) {
            // Keep a usable estimate if only the fresh burst met temporary packet loss.
        } finally {
            foreach(var request in requests){_freshProbes.TryRemove(request.Id,out _);_probes.TryRemove(request.Id,out _);}
        }
    }
    private async Task SendProbeAsync(int id,CancellationToken token)
    {
        await _send.WaitAsync(token).ConfigureAwait(false);
        try {
            var ws=_socket;
            if(ws?.State!=WebSocketState.Open)throw new IOException("Service is disconnected");
            var t1=_clock.NowMs;_probes[id]=t1;
            var bytes=JsonSerializer.SerializeToUtf8Bytes(new{type="sync",id,t1});
            await ws.SendAsync(bytes.AsMemory(),WebSocketMessageType.Text,true,token).ConfigureAwait(false);
        } finally {_send.Release();}
    }
    private async Task ConfirmAsync(object message)
    {
        var requestId=Guid.NewGuid().ToString("N");var response=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[requestId]=response;
        try {
            var payload=JsonSerializer.SerializeToElement(message,Json).EnumerateObject().ToDictionary(p=>p.Name,p=>(object?)p.Value.Clone());payload["requestId"]=requestId;
            await SendAsync(payload).ConfigureAwait(false);
            await response.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        } finally {_requests.TryRemove(requestId,out _);}
    }
    public async Task LeaveRoomAsync(bool closeRoom=false,CancellationToken token=default)
    {
        if(Session is null)return;
        var response=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(Interlocked.CompareExchange(ref _roomExit,response,null) is not null)throw new InvalidOperationException("正在退出房间，请稍候。");
        _closeRequested=closeRoom;_expectedClosure=null;UnconfirmedRoomExits=0;
        try {
            await SendAsync(new{type=closeRoom?"close-room":"leave"}).ConfigureAwait(false);
            await response.Task.WaitAsync(TimeSpan.FromSeconds(5),token).ConfigureAwait(false);
        } finally {Interlocked.CompareExchange(ref _roomExit,null,response);_closeRequested=false;_expectedClosure=null;}
    }
    private async Task<SessionIdentity> RequestRoomAsync(object payload, CancellationToken token)
    {
        if (Session is not null) throw new InvalidOperationException("ALREADY_IN_ROOM");
        var pending = new TaskCompletionSource<SessionIdentity>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _roomRequest, pending, null) is not null) throw new InvalidOperationException("正在创建或加入房间，请稍候。");
        try
        {
            await SendAsync(payload).ConfigureAwait(false);
            return await pending.Task.WaitAsync(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            // A late response must not silently join a room after the UI reports cancellation.
            await DisconnectAsync().ConfigureAwait(false); throw;
        }
        finally { Interlocked.CompareExchange(ref _roomRequest, null, pending); }
    }
    public async Task ConnectAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss") || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("Use a ws:// or wss:// service address", nameof(endpoint));
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await DisconnectCore().ConfigureAwait(false);
            if (Endpoint != uri.AbsoluteUri)
            {
                _session = null; _snapshot = null; _activeSession = null;
                _scheduler.CancelAll(); _prepared.Clear(); _committed.Clear();
            }
            Endpoint = uri.AbsoluteUri;
            if (_session is { } old && old.Endpoint != Endpoint) _session = null;
            _restoringRoom=_session is not null;
            _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _connection = new(); _socket = new();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _connection.Token);
            try
            {
                await _socket.ConnectAsync(uri, linked.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _lastInboundMs,_clock.NowMs);
                _receiveTask = ReceiveLoop(_socket, _connection.Token);
                await _hello.Task.WaitAsync(TimeSpan.FromSeconds(10), linked.Token).ConfigureAwait(false);
                _syncTask = SyncLoop(_connection.Token);
                if (_session is { } session) {
                    _resumeIdentity=session;_resumeIdentityConfirmed=false;
                    var resume=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);_roomResume=resume;
                    try {
                        await SendAsync(new { type = "resume", code = session.Code, token = session.Token }).ConfigureAwait(false);
                        await resume.Task.WaitAsync(TimeSpan.FromSeconds(10),linked.Token).ConfigureAwait(false);
                    }finally{_roomResume=null;_resumeIdentity=null;_resumeIdentityConfirmed=false;}
                    _restoringRoom=false;
                    if(RoomRestored is{} handlers)foreach(Action<SessionIdentity> handler in handlers.GetInvocationList())
                        try{handler(session);}catch(Exception error){Trace.WriteLine(error.GetType().Name);}
                } else _restoringRoom=false;
                FireChanged();
            }
            catch(OperationCanceledException error) when(!cancellationToken.IsCancellationRequested) {
                await DisconnectCore().ConfigureAwait(false);throw new IOException("连接中断，房间恢复尚未完成。",error);
            }
            catch { await DisconnectCore().ConfigureAwait(false); throw; }
        }
        finally { _lifecycle.Release(); }
    }
    public async Task DisconnectAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await DisconnectCore().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }
    public void ForgetDisconnectedRoom()
    {
        if (Connected) throw new InvalidOperationException("请先断开连接，再清除本机房间身份。");
        _session = null; _snapshot = null; _activeSession = null;
        _scheduler.RoomExited(); FireChanged();
    }
    private async Task DisconnectCore()
    {
        CancelPreparations();
        FailRequests();
        _roomRequest?.TrySetException(new IOException("Service is disconnected"));
        _connected = false; _connection?.Cancel(); _socket?.Abort();
        foreach (var task in new[] { _receiveTask, _syncTask }) if (task is not null)
            try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (WebSocketException) { }
        _socket?.Dispose(); _connection?.Dispose();
        _socket = null; _connection = null; _receiveTask = _syncTask = null;
        _restoringRoom=false;
        _probes.Clear(); _roomClock.Reset(); _scheduler.TransportDisconnected(); FireChanged();
    }
    public async Task SendAsync(object payload)
    {
        var token = _connection?.Token ?? CancellationToken.None;
        await _send.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var ws = _socket;
            if (ws?.State != WebSocketState.Open) throw new IOException("Service is disconnected");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
            await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
        }
        finally { _send.Release(); }
    }
    private async Task SyncLoop(CancellationToken token)
    {
        try
        {
            var initial = 0;
            while (!token.IsCancellationRequested)
            {
                if(_clock.NowMs-Volatile.Read(ref _lastInboundMs)>_inactivityTimeoutMs) {
                    _connected=false;_socket?.Abort();_connection?.Cancel();_scheduler.TransportDisconnected();_roomClock.Reset();
                    FireNotice("房间连接已中断，请点击重新连接；房主也可关闭房间。");FireChanged();return;
                }
                foreach (var probe in _probes) if (_clock.NowMs - probe.Value > 10_000) _probes.TryRemove(probe.Key, out _);
                await SendProbeAsync(Interlocked.Increment(ref _syncId),token).ConfigureAwait(false);
                await Task.Delay(initial++ < 7 ? 60 : 500, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { FireNotice("房间连接已中断，请点击重新连接。"); }
    }
    private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[8192];
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 65_536) throw new InvalidDataException("Unsupported service message");
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                var t4 = _clock.NowMs;
                Interlocked.Exchange(ref _lastInboundMs,t4);
                using var document = JsonDocument.Parse(message.ToArray());
                await Handle(document.RootElement, t4).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch(Exception) when(token.IsCancellationRequested) { }
        catch (Exception error) { _hello?.TrySetException(error); FireNotice("房间连接已中断，请点击重新连接。"); }
        finally
        {
            CancelPreparations();
            FailRequests();
            _roomRequest?.TrySetException(new IOException("Service is disconnected"));
            _connected = false; _hello?.TrySetException(new IOException("Handshake or connection ended"));
            _connection?.Cancel(); _scheduler.TransportDisconnected(); _roomClock.Reset(); FireChanged();
        }
    }
    private async Task Handle(JsonElement root, double t4)
    {
        var type = root.GetProperty("type").GetString();
        switch (type)
        {
            case "hello":
            {
                if (root.GetProperty("product").GetString() != "MabiSync" || root.GetProperty("protocolVersion").GetInt32() != 1) throw new InvalidDataException("Incompatible room product or protocol");
                SupportsGameObservation = root.TryGetProperty("features", out var features) && features.TryGetProperty("gameObservation", out var observation) && observation.ValueKind == JsonValueKind.Number && observation.GetInt32() == 1;
                SupportsFirstCycleStop = features.ValueKind == JsonValueKind.Object && features.TryGetProperty("firstCycleStop", out var cycle) && cycle.ValueKind == JsonValueKind.Number && cycle.GetInt32() == 1;
                SupportsRosterEditing=features.ValueKind==JsonValueKind.Object && features.TryGetProperty("rosterEditing",out var edit) && edit.ValueKind==JsonValueKind.Number && edit.GetInt32()==1;
                var epoch = root.GetProperty("serverEpoch").GetString()!;
                if (!Guid.TryParse(epoch, out _)) throw new InvalidDataException("Invalid service epoch");
                if (_epoch != epoch)
                {
                    CancelPreparations();
                    _epoch = epoch; _scheduler.ChangeEpoch(epoch); _prepared.Clear(); _committed.Clear(); _activeSession = null; _snapshot = null;
                    if (_session?.ServerEpoch != epoch) _session = null;
                }
                await SendAsync(new { type = "hello", product = "MabiSync", protocolVersion = 1 }).ConfigureAwait(false); break;
            }
            case "hello-accepted":
                if (root.GetProperty("serverEpoch").GetString() != _epoch) throw new InvalidDataException("Service restarted during handshake");
                _connected = true; _hello?.TrySetResult(true); FireChanged(); break;
            case "sync":
                if (root.GetProperty("serverEpoch").GetString() == _epoch && _probes.TryRemove(root.GetProperty("id").GetInt32(), out var t1) && root.GetProperty("t1").GetDouble() == t1)
                {
                    var accepted=_roomClock.AddSample(t1,root.GetProperty("t2").GetDouble(),root.GetProperty("t3").GetDouble(),t4);
                    if(_freshProbes.TryRemove(root.GetProperty("id").GetInt32(),out var reply))reply.TrySetResult(accepted);
                    FireChanged();
                }
                break;
            case "session":
                if (root.GetProperty("serverEpoch").GetString() != _epoch) break;
                if(_resumeIdentity is{} expected && (root.GetProperty("code").GetString()!=expected.Code || root.GetProperty("memberId").GetString()!=expected.MemberId || root.GetProperty("token").GetString()!=expected.Token))
                    throw new InvalidDataException("恢复的房间身份与原身份不一致。");
                _session = new(root.GetProperty("code").GetString()!, root.GetProperty("memberId").GetString()!, root.GetProperty("token").GetString()!, _epoch!, Endpoint);
                if(_roomResume is not null)_resumeIdentityConfirmed=true;
                _roomRequest?.TrySetResult(_session);
                FireChanged(); break;
            case "state":
                if (root.GetProperty("serverEpoch").GetString() == _epoch) {
                    var room=root.GetProperty("room").Clone();Volatile.Write(ref _snapshot,new(room));
                    if(_resumeIdentityConfirmed && _resumeIdentity is{} resumed && room.GetProperty("code").GetString()==resumed.Code &&
                        room.GetProperty("members").EnumerateArray().Any(m=>m.GetProperty("id").GetString()==resumed.MemberId && m.GetProperty("connected").GetBoolean()))_roomResume?.TrySetResult(true);
                    FireChanged();
                }
                break;
            case "prepare-action":
            {
                var action = root.GetProperty("action").Deserialize<ScheduledAction>(Json)! with { Mode = root.GetProperty("mode").GetString()! };
                QueuePreparation(action,false);break;
            }
            case "prepare-session":
            {
                var sessionId = root.GetProperty("sessionId").GetString()!;
                var mode = root.GetProperty("mode").GetString()!;
                var epoch = root.GetProperty("serverEpoch").GetString()!;
                QueuePreparation(new("manual:"+sessionId,sessionId,"stop",0,epoch){Mode=mode},true);break;
            }
            case "cancel-session":
                if (root.GetProperty("serverEpoch").GetString() == _epoch) {
                    var cancelledSession=root.GetProperty("sessionId").GetString();CancelPreparations(sessionId:cancelledSession);
                    if(cancelledSession==_activeSession)_activeSession=null;
                }
                break;
            case "commit-action":
            {
                var wire = root.GetProperty("action").Deserialize<ScheduledAction>(Json)!;
                if (!_prepared.TryGetValue(wire.Id, out var prepared)) break;
                var action = wire with { Mode = prepared.Mode };
                if (action != prepared || !_committed.TryAdd(action.Id, 0)) break;
                if (!_scheduler.Commit(action))
                {
                    _scheduler.CancelAction(action.Id);
                    FireNotice("Commit rejected locally; no input was dispatched");
                }
                break;
            }
            case "cancel-action":
                if (root.GetProperty("serverEpoch").GetString() == _epoch) {
                    var cancelledAction=root.GetProperty("actionId").GetString()!;CancelPreparations(actionId:cancelledAction);_scheduler.CancelAction(cancelledAction);
                }
                break;
            case "stop-now":
                if (root.GetProperty("serverEpoch").GetString() == _epoch && root.GetProperty("sessionId").GetString() == _activeSession)
                    _scheduler.StopNow(root.GetProperty("id").GetString()!, _activeSession!, _epoch!, _activeMode);
                break;
            case "room-closed": case "left": case "kicked":
                if(root.GetProperty("serverEpoch").GetString()!=_epoch)break;
                CancelPreparations();
                _session = null; _snapshot = null; _activeSession = null; _scheduler.RoomExited();
                var needsAcknowledgment=type=="room-closed" && root.TryGetProperty("closureId",out var closureId);
                if(needsAcknowledgment) {
                    var closure=root.GetProperty("closureId").GetString()!;
                    if(_closeRequested)_expectedClosure=closure;
                    await SendAsync(new{type="room-closed-ack",closureId=closure}).ConfigureAwait(false);
                }
                if(!_closeRequested || !needsAcknowledgment)_roomExit?.TrySetResult(true);
                if (type == "kicked") FireNotice("你已被房主移出房间。");
                else FireNotice(type=="room-closed"?"房主已关闭房间，你已退出。":"已离开房间。");
                FireChanged(); break;
            case "room-close-complete":
                if(root.GetProperty("serverEpoch").GetString()==_epoch && _closeRequested && root.GetProperty("closureId").GetString()==_expectedClosure) {
                    UnconfirmedRoomExits=root.GetProperty("unconfirmedMembers").GetInt32();_roomExit?.TrySetResult(true);
                }
                break;
            case "request-complete":
                if(root.GetProperty("serverEpoch").GetString()==_epoch && _requests.TryGetValue(root.GetProperty("requestId").GetString()!,out var completed))completed.TrySetResult(true);
                break;
            case "error":
                if(root.TryGetProperty("requestId",out var req) && _requests.TryGetValue(req.GetString()!,out var failed)) {
                    failed.TrySetException(new InvalidOperationException(root.GetProperty("code").GetString()));break;
                }
                _roomRequest?.TrySetException(new InvalidOperationException(root.GetProperty("code").GetString() ?? "Service error"));
                _roomResume?.TrySetException(new InvalidOperationException(root.GetProperty("code").GetString() ?? "Service error"));
                if (root.GetProperty("code").GetString() is "SESSION_EXPIRED" or "ROOM_NOT_FOUND") { _session = null; _snapshot = null; }
                FireNotice(root.GetProperty("code").GetString() ?? "Service error");FireChanged(); break;
        }
    }
    private void FailRequests()
    {
        _roomResume?.TrySetException(new IOException("连接中断，房间身份尚未恢复。"));
        _roomExit?.TrySetException(new IOException("连接中断，未收到房间退出确认。请重连后核对。"));
        foreach(var pending in _requests.Values)pending.TrySetException(new IOException("连接中断，列表修改尚未确认。请重连后核对。"));
    }
    private async Task ReceiptLoop()
    {
        await foreach (var receipt in _receipts.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            _prepared.TryRemove(receipt.ActionId, out _);
            _committed.TryRemove(receipt.ActionId, out _);
            try
            {
                if (receipt.ReportToRoom && Connected && receipt.SessionId == _activeSession) await SendAsync(new { type = "receipt", actionId = receipt.ActionId, status = receipt.Status,
                    actualServerMs = receipt.ActualServerMs, detail = receipt.Detail, simulated = receipt.Simulated }).ConfigureAwait(false);
            }
            catch (Exception) when(!Connected || _connection?.IsCancellationRequested==true) { /* Keep expected offline receipts locally. */ }
            catch (Exception error) { FireNotice("Receipt retained locally: " + error.GetType().Name); }
            if (ReceiptReported is { } handlers) foreach (Action<ActionReceipt> handler in handlers.GetInvocationList())
                try { handler(receipt); } catch (Exception error) { Trace.WriteLine(error.GetType().Name); }
        }
    }
    private void FireChanged()
    {
        if (Changed is { } handlers) foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch (Exception error) { Trace.WriteLine(error.GetType().Name); }
    }
    private void FireNotice(string message)
    {
        if (Notice is { } handlers) foreach (Action<string> handler in handlers.GetInvocationList())
            try { handler(message); } catch (Exception error) { Trace.WriteLine(error.GetType().Name); }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _scheduler.Receipt -= QueueReceipt;
        await DisconnectAsync().ConfigureAwait(false);
        _preparationQueue.Writer.TryComplete();await _preparationPump.ConfigureAwait(false);
        _receipts.Writer.TryComplete(); await _receiptPump.ConfigureAwait(false);
        _send.Dispose(); _lifecycle.Dispose();
    }
}
