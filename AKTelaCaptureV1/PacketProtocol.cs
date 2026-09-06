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
    public const int BatchHeader = 8;
    public const int MaxBatchPackets = 32;

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
    public static long TimestampUs(byte[] packet) => packet.Length >= Header
        ? BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(8, 8))
        : long.MaxValue;

    public static byte[] CreateBatch(IReadOnlyList<byte[]> packets)
    {
        if (packets.Count is < 1 or > MaxBatchPackets)
            throw new ArgumentOutOfRangeException(nameof(packets));

        var length = BatchHeader;
        foreach (var packet in packets)
        {
            if (packet.Length < Header) throw new ArgumentException("Pacote de mídia inválido.", nameof(packets));
            length = checked(length + sizeof(int) + packet.Length);
        }

        var batch = new byte[length];
        batch[0] = (byte)'A'; batch[1] = (byte)'K'; batch[2] = (byte)'B'; batch[3] = (byte)'1';
        batch[4] = 1;
        batch[5] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(batch.AsSpan(6, 2), (ushort)packets.Count);

        var offset = BatchHeader;
        foreach (var packet in packets)
        {
            BinaryPrimitives.WriteInt32LittleEndian(batch.AsSpan(offset, 4), packet.Length);
            offset += 4;
            packet.CopyTo(batch, offset);
            offset += packet.Length;
        }
        return batch;
    }
}
