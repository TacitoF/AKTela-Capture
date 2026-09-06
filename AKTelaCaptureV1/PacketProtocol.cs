using System.Buffers.Binary;
using System.Diagnostics;

namespace AKTelaCapture;

internal enum MediaKind : byte { Video = 1, Audio = 2 }

internal static class MediaClock
{
    private static readonly long Start = Stopwatch.GetTimestamp();
    public static long NowMicroseconds() => (long)((Stopwatch.GetTimestamp() - Start) * 1_000_000d / Stopwatch.Frequency);
}

internal static class PacketProtocol
{
    public const int Header = 24;

    public static byte[] Create(MediaKind kind, bool keyframe, long timestampUs, int durationUs, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[Header + payload.Length];
        packet[0] = (byte)'A'; packet[1] = (byte)'K'; packet[2] = (byte)'V'; packet[3] = (byte)'5';
        packet[4] = 5;
        packet[5] = (byte)kind;
        packet[6] = keyframe ? (byte)1 : (byte)0;
        packet[7] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(8, 8), timestampUs);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(16, 4), durationUs);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(20, 4), payload.Length);
        payload.CopyTo(packet.AsSpan(Header));
        return packet;
    }

    public static MediaKind Kind(byte[] packet) => packet.Length > 5 ? (MediaKind)packet[5] : MediaKind.Video;
    public static bool IsKeyframe(byte[] packet) => packet.Length > 6 && (packet[6] & 1) != 0;
}
