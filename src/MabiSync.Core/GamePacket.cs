using System.Buffers.Binary;
using System.Net;

namespace MabiSync.Core;

public sealed record GameTcpPacket(string SourceAddress, ushort SourcePort, string DestinationAddress, ushort DestinationPort,
    uint Sequence, byte Flags, byte[] Payload);

public static class GamePacket
{
    public static GameTcpPacket? Read(int link, ReadOnlySpan<byte> raw)
    {
        var at = 0;
        if (link == 1)
        {
            if (raw.Length < 14) return null;
            at = 14; var ether = BinaryPrimitives.ReadUInt16BigEndian(raw[12..]);
            while (ether is 0x8100 or 0x88a8)
            { if (raw.Length - at < 4) return null; ether = BinaryPrimitives.ReadUInt16BigEndian(raw[(at + 2)..]); at += 4; }
            if (ether != 0x800) return null;
        }
        else if (link == 0)
        { if (raw.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(raw) != 2) return null; at = 4; }
        else if (link is not (12 or 101)) return null;
        if (raw.Length - at < 20 || raw[at] >> 4 != 4 || raw[at + 9] != 6) return null;
        var ipLength = (raw[at] & 15) * 4; var total = BinaryPrimitives.ReadUInt16BigEndian(raw[(at + 2)..]);
        if (ipLength < 20 || total < ipLength + 20 || total > raw.Length - at || (BinaryPrimitives.ReadUInt16BigEndian(raw[(at + 6)..]) & 0x3fff) != 0) return null;
        var tcp = at + ipLength; var tcpLength = (raw[tcp + 12] >> 4) * 4;
        if (tcpLength < 20 || tcpLength > total - ipLength) return null;
        return new(new IPAddress(raw.Slice(at + 12, 4)).ToString(), BinaryPrimitives.ReadUInt16BigEndian(raw[tcp..]),
            new IPAddress(raw.Slice(at + 16, 4)).ToString(), BinaryPrimitives.ReadUInt16BigEndian(raw[(tcp + 2)..]),
            BinaryPrimitives.ReadUInt32BigEndian(raw[(tcp + 4)..]), raw[tcp + 13], raw.Slice(tcp + tcpLength, total - ipLength - tcpLength).ToArray());
    }
}
