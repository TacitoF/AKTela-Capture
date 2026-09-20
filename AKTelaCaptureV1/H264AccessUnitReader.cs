namespace AKTelaCapture;

internal sealed record H264StreamInfo(int ProfileIdc, int Constraints, int LevelIdc, string CodecString, string ProfileName)
{
    public static string NameForProfileIdc(int value) => value switch
    {
        66 => "baseline",
        77 => "main",
        88 => "extended",
        100 => "high",
        110 => "high10",
        122 => "high422",
        244 => "high444",
        _ => $"profile-{value}"
    };
}

/// <summary>
/// A compacting byte buffer for the streaming parsers. Unlike List.RemoveRange,
/// consuming a frame is O(1) and does not copy the remainder on every packet.
/// </summary>
internal sealed class StreamingByteBuffer
{
    private byte[] _data;
    private int _start;
    private int _end;

    public StreamingByteBuffer(int initialCapacity) => _data = new byte[initialCapacity];

    public int Length => _end - _start;
    public ReadOnlySpan<byte> Span => new(_data, _start, Length);

    public void Append(byte[] source, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length > source.Length) throw new ArgumentOutOfRangeException(nameof(length));
        EnsureWritable(length);
        Buffer.BlockCopy(source, 0, _data, _end, length);
        _end += length;
    }

    public byte[] SliceToArray(int offset, int length)
    {
        if ((uint)offset > (uint)Length || (uint)length > (uint)(Length - offset))
            throw new ArgumentOutOfRangeException(nameof(length));
        var result = new byte[length];
        Buffer.BlockCopy(_data, _start + offset, result, 0, length);
        return result;
    }

    public void Consume(int length)
    {
        if ((uint)length > (uint)Length) throw new ArgumentOutOfRangeException(nameof(length));
        _start += length;
        if (_start == _end) _start = _end = 0;
    }

    public void KeepTail(int length)
    {
        length = Math.Clamp(length, 0, Length);
        Consume(Length - length);
    }

    private void EnsureWritable(int length)
    {
        if (length <= _data.Length - _end) return;

        var current = Length;
        if (_start > 0 && length <= _data.Length - current)
        {
            Buffer.BlockCopy(_data, _start, _data, 0, current);
            _start = 0;
            _end = current;
            return;
        }

        var required = checked(current + length);
        var capacity = _data.Length;
        while (capacity < required) capacity = checked(capacity * 2);
        var replacement = new byte[capacity];
        Buffer.BlockCopy(_data, _start, replacement, 0, current);
        _data = replacement;
        _start = 0;
        _end = current;
    }
}

internal sealed class H264AccessUnitReader
{
    private const int MaxBufferedBytes = 4 * 1024 * 1024;
    private const int TailBytesOnOverflow = 1024 * 1024;

    private readonly StreamingByteBuffer _buffer = new(512 * 1024);
    private byte[]? _sps;
    private byte[]? _pps;

    public H264StreamInfo? StreamInfo { get; private set; }

    public IEnumerable<(byte[] Data, bool Keyframe)> Push(byte[] bytes, int length)
    {
        _buffer.Append(bytes, length);
        var output = new List<(byte[], bool)>();

        while (true)
        {
            var data = _buffer.Span;
            var first = FindAud(data, 0);
            if (first < 0) break;
            if (first > 0)
            {
                // Some encoders emit SPS/PPS before the first AUD. Keep them so
                // the first relayed IDR remains independently decodable.
                CacheSets(data[..first]);
                _buffer.Consume(first);
                data = _buffer.Span;
            }

            var next = FindAud(data, 3);
            if (next < 0) break;

            var unit = _buffer.SliceToArray(0, next);
            _buffer.Consume(next);
            var nals = CacheSets(unit);
            var keyframe = (nals & (1u << 5)) != 0;
            var hasSps = (nals & (1u << 7)) != 0;
            var hasPps = (nals & (1u << 8)) != 0;
            if (keyframe && (!hasSps || !hasPps) && _sps is not null && _pps is not null)
                unit = PrependSets(unit, _sps, _pps);

            output.Add((unit, keyframe));
        }

        if (_buffer.Length > MaxBufferedBytes)
            _buffer.KeepTail(TailBytesOnOverflow);

        return output;
    }

    public static H264StreamInfo? Inspect(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i + 7 < data.Length; i++)
        {
            var sc = StartCode(data, i);
            if (sc == 0) continue;
            var nal = i + sc;
            if (nal + 3 >= data.Length || (data[nal] & 0x1f) != 7) continue;

            var profile = data[nal + 1];
            var constraints = data[nal + 2];
            var level = data[nal + 3];
            var codec = $"avc1.{profile:X2}{constraints:X2}{level:X2}";
            return new H264StreamInfo(profile, constraints, level, codec, H264StreamInfo.NameForProfileIdc(profile));
        }
        return null;
    }

    private uint CacheSets(ReadOnlySpan<byte> data)
    {
        uint flags = 0;
        var searchFrom = 0;
        while (true)
        {
            var start = FindStartCode(data, searchFrom);
            if (start < 0) break;
            var startCodeLength = StartCode(data, start);
            var header = start + startCodeLength;
            if (header >= data.Length) break;

            var next = FindStartCode(data, header + 1);
            var end = next >= 0 ? next : data.Length;
            var type = data[header] & 0x1f;
            flags |= 1u << type;

            if (type is 7 or 8)
            {
                var copy = data[start..end].ToArray();
                if (type == 7)
                {
                    _sps = copy;
                    StreamInfo = Inspect(copy) ?? StreamInfo;
                }
                else
                {
                    _pps = copy;
                }
            }

            if (next < 0) break;
            searchFrom = next;
        }
        return flags;
    }

    private static byte[] PrependSets(byte[] unit, byte[] sps, byte[] pps)
    {
        var result = new byte[sps.Length + pps.Length + unit.Length];
        Buffer.BlockCopy(sps, 0, result, 0, sps.Length);
        Buffer.BlockCopy(pps, 0, result, sps.Length, pps.Length);
        Buffer.BlockCopy(unit, 0, result, sps.Length + pps.Length, unit.Length);
        return result;
    }

    private static int FindAud(ReadOnlySpan<byte> data, int start)
    {
        var searchFrom = Math.Max(0, start);
        while (true)
        {
            var position = FindStartCode(data, searchFrom);
            if (position < 0) return -1;
            var header = position + StartCode(data, position);
            if (header < data.Length && (data[header] & 0x1f) == 9) return position;
            searchFrom = header + 1;
        }
    }

    private static int FindStartCode(ReadOnlySpan<byte> data, int start)
    {
        for (var i = Math.Max(0, start); i + 2 < data.Length; i++)
            if (StartCode(data, i) != 0) return i;
        return -1;
    }

    private static int StartCode(ReadOnlySpan<byte> data, int i)
    {
        if (i + 3 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1) return 4;
        if (i + 2 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1) return 3;
        return 0;
    }
}
