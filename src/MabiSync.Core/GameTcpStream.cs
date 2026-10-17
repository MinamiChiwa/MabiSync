using System.Buffers.Binary;

namespace MabiSync.Core;

/// <summary>Bounded, single-direction TCP reassembly and game framing; no padding across gaps.</summary>
public sealed class GameTcpStream(Action<int, byte[]> onFrame, Action<string> invalidate)
{
    private readonly List<byte> _buffer = [], _history = [];
    private readonly List<(uint Seq, byte[] Bytes, double Time)> _pending = [];
    private readonly List<(int Count, double Time)> _times = [];
    public double? FrameReceivedLocalMs { get; private set; }
    private uint _next;
    private bool _started, _framed;
    private double? _gapSince;
    public void Restart(string reason)
    {
        _buffer.Clear(); _history.Clear(); _pending.Clear(); _times.Clear(); FrameReceivedLocalMs = null; _started = _framed = false; _gapSince = null; invalidate(reason);
    }
    public void Feed(uint sequence, ReadOnlySpan<byte> payload, double nowMs, double? receivedLocalMs = null)
    {
        if (payload.IsEmpty) return;
        if (payload.Length > 65536) { Restart("截获的数据段过长，等待重新识别。"); return; }
        if (!_started) { _started = true; _next = sequence; }
        var delta = unchecked((int)(sequence - _next));
        if (delta > 0)
        {
            if (_pending.Count >= 128 || _pending.Sum(p => p.Bytes.Length) + payload.Length > 1024 * 1024)
            { Restart("已清理积压的游戏数据，等待重新识别。"); return; }
            var duplicate = false;
            foreach (var row in _pending) if (row.Seq == sequence && payload.SequenceEqual(row.Bytes)) { duplicate = true; break; }
            if (!duplicate) _pending.Add((sequence, payload.ToArray(), receivedLocalMs ?? nowMs));
            _gapSince ??= nowMs; CheckGap(nowMs); return;
        }
        Append(sequence, payload, receivedLocalMs ?? nowMs);
        if (!_started) return;
        while (true)
        {
            var at = _pending.FindIndex(p => unchecked((int)(p.Seq - _next)) <= 0);
            if (at < 0) break;
            var row = _pending[at]; _pending.RemoveAt(at); Append(row.Seq, row.Bytes, row.Time);
            if (!_started) return;
        }
        if (_pending.Count == 0) _gapSince = null;
    }
    public void CheckGap(double nowMs)
    {
        if (_gapSince is not { } since || nowMs - since < 1500 || _pending.Count == 0) return;
        var first = _pending.MinBy(p => unchecked((int)(p.Seq - _next)));
        Restart("部分游戏数据未能抓到，需要重新识别昵称和加入顺序。");
        Feed(first.Seq, first.Bytes, nowMs, first.Time);
    }
    private void Append(uint sequence, ReadOnlySpan<byte> payload, double time)
    {
        var delta = unchecked((int)(sequence - _next));
        var skip = (int)Math.Min(payload.Length, -(long)delta);
        // Compare overlapping bytes still in our bounded history, including full retransmits.
        for (var i = 0; i < skip; i++)
        {
            var offset = (long)_history.Count + delta + i;
            if (offset >= 0 && offset < _history.Count && _history[(int)offset] != payload[i])
            { Restart("重复收到的游戏数据不一致，需要重新识别。"); return; }
        }
        var bytes = payload[skip..].ToArray();
        _next = unchecked(_next + (uint)bytes.Length);
        _history.AddRange(bytes); if (_history.Count > 65536) _history.RemoveRange(0, _history.Count - 65536);
        _buffer.AddRange(bytes);
        if (bytes.Length > 0) _times.Add((bytes.Length, time));
        if (_buffer.Count > TwGameProtocol.MaximumBody + 65545) { Restart("游戏帧过长，等待重新识别。"); return; }
        Drain();
    }
    private (int Code, int Length, byte Flag)? Header(byte[] data, int offset)
    {
        if (offset < 0 || data.Length - offset < 9) return null;
        var code = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
        var length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4)); var flag = data[offset + 8];
        return TwGameProtocol.KnownProtocols.Contains(code) && length >= 0 && length <= TwGameProtocol.MaximumBody && flag <= 1 ? (code, length, flag) : null;
    }
    private void Drain()
    {
        var data = _buffer.ToArray(); var at = 0;
        while (data.Length - at >= 9)
        {
            var header = Header(data, at);
            if (!_framed)
            {
                if (header is not { } candidate) { at++; continue; }
                var second = (long)at + 9 + candidate.Length;
                if (second + 9 > data.Length) break;
                if (Header(data, (int)second) is null) { at++; continue; }
                _framed = true;
            }
            else if (header is null)
            { _framed = false; invalidate("游戏数据不连续，需要重新识别。"); at++; continue; }
            var frame = header!.Value;
            if (data.Length - at - 9 < frame.Length) break;
            var body = data.AsSpan(at + 9, frame.Length);
            // Ignore unrelated payloads without expanding or retaining them.
            if (frame.Code is 20047 or 20520 or 20533 or 120412 or 120413 or 120692 or 120693 or 20238 or 20307)
            {
                double? received = null; var offset = 0;
                foreach (var span in _times)
                {
                    if (offset < at + 9 + frame.Length && offset + span.Count > at)
                        received = received is null ? span.Time : Math.Max(received.Value, span.Time);
                    offset += span.Count;
                }
                FrameReceivedLocalMs = received is { } stamp && double.IsFinite(stamp) ? stamp : null;
                try { onFrame(frame.Code, TwGameProtocol.Expand(body, frame.Flag)); }
                catch (Exception error) when (error is InvalidDataException or System.Text.DecoderFallbackException)
                { invalidate("无法解析本次游戏数据，需要重新识别。"); }
            }
            at += 9 + frame.Length;
        }
        if (!_framed && at > 65536) at = Math.Max(at, data.Length - 8);
        if (at > 0)
        {
            _buffer.RemoveRange(0, at);
            var remove = at;
            while (remove > 0 && _times.Count > 0)
            {
                var span = _times[0];
                if (span.Count <= remove) { remove -= span.Count; _times.RemoveAt(0); }
                else { _times[0] = (span.Count - remove, span.Time); remove = 0; }
            }
        }
    }
}
