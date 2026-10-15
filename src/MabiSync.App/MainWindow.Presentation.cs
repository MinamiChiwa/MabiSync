using System.Text.Json;
using MabiSync.Client;

namespace MabiSync.App;

public partial class MainWindow
{
    private int _viewDepth;
    private JsonElement? _viewRoom;
    private SessionIdentity? _viewSession;
    private bool _viewConnected;
    // A network callback may publish another immutable room while a frame renders.
    // Every UI helper in that frame must use the same room and identity.
    private bool InRoomView => Dispatcher.CheckAccess() && _viewDepth > 0;
    private JsonElement? ViewRoom => InRoomView ? _viewRoom : _client.Room;
    private SessionIdentity? ViewSession => InRoomView ? _viewSession : _client.Session;
    private bool ViewConnected => InRoomView ? _viewConnected : _client.Connected;
    private void WithRoomView(Action render)
    {
        if (_viewDepth == 0)
        {
            _viewSession = _client.Session; _viewRoom = _client.Room; _viewConnected = _client.Connected;
            if (_viewSession is null || _viewRoom is not { } room ||
                room.GetProperty("code").GetString() != _viewSession.Code ||
                !room.GetProperty("members").EnumerateArray().Any(m => m.GetProperty("id").GetString() == _viewSession.MemberId))
                _viewRoom = null;
        }
        _viewDepth++;
        try { render(); }
        finally { _viewDepth--; }
    }
}
