using System.Buffers.Binary;
using System.Diagnostics;

namespace AKTelaCapture;

internal enum MediaKind : byte
{
    Video = 1,
    Audio = 2
}

internal static class MediaClock
{
    private static readonly long Start = Stopwatch.GetTimestamp();
    public static long NowMicroseconds()
    {
        var elapsed = Stopwatch.GetTimestamp() - Start;
        return (long)(elapsed * 1_000_000d / Stopwatch.Frequency);
    }
}

internal static class MediaPacket
{
    private const int HeaderSize = 24;

    public static byte[] Create(MediaKind kind, bool keyFrame, long timestampUs, int durationUs, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[HeaderSize + payload.Length];
        packet[0] = (byte)'A';
        packet[1] = (byte)'K';
        packet[2] = (byte)'V';
        packet[3] = (byte)'3';
        packet[4] = 1; // protocolo
        packet[5] = (byte)kind;
        packet[6] = keyFrame ? (byte)1 : (byte)0;
        packet[7] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(8, 8), timestampUs);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(16, 4), durationUs);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(20, 4), payload.Length);
        payload.CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }
}
