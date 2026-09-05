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

internal sealed class H264AccessUnitReader
{
    private readonly List<byte> _buffer = new(512 * 1024);
    private byte[]? _sps;
    private byte[]? _pps;

    public H264StreamInfo? StreamInfo { get; private set; }

    public IEnumerable<(byte[] Data, bool Keyframe)> Push(byte[] bytes, int length)
    {
        for (var i = 0; i < length; i++) _buffer.Add(bytes[i]);
        var output = new List<(byte[], bool)>();

        while (true)
        {
            var first = FindAud(_buffer, 0);
            if (first < 0) break;
            if (first > 0)
            {
                // Alguns encoders emitem SPS/PPS antes do primeiro AUD. Preserve esses parâmetros
                // para que o primeiro IDR enviado no formato Annex B seja autocontido.
                CacheSets(_buffer.GetRange(0, first).ToArray());
                _buffer.RemoveRange(0, first);
            }

            var next = FindAud(_buffer, 4);
            if (next < 0) break;

            var unit = _buffer.GetRange(0, next).ToArray();
            _buffer.RemoveRange(0, next);
            CacheSets(unit);

            var key = ContainsNal(unit, 5);
            if (key && (!ContainsNal(unit, 7) || !ContainsNal(unit, 8)) && _sps is not null && _pps is not null)
                unit = PrependSets(unit, _sps, _pps);

            output.Add((unit, key));
        }

        if (_buffer.Count > 4 * 1024 * 1024)
            _buffer.RemoveRange(0, _buffer.Count - 1024 * 1024);

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

    private void CacheSets(byte[] data)
    {
        foreach (var nal in Nals(data))
        {
            if (nal.Type == 7)
            {
                _sps = nal.Bytes;
                StreamInfo = Inspect(nal.Bytes) ?? StreamInfo;
            }
            else if (nal.Type == 8)
            {
                _pps = nal.Bytes;
            }
        }
    }

    private static byte[] PrependSets(byte[] unit, byte[] sps, byte[] pps)
    {
        var result = new byte[sps.Length + pps.Length + unit.Length];
        Buffer.BlockCopy(sps, 0, result, 0, sps.Length);
        Buffer.BlockCopy(pps, 0, result, sps.Length, pps.Length);
        Buffer.BlockCopy(unit, 0, result, sps.Length + pps.Length, unit.Length);
        return result;
    }

    private static bool ContainsNal(byte[] data, int type) => Nals(data).Any(n => n.Type == type);

    private static IEnumerable<(int Type, byte[] Bytes)> Nals(byte[] data)
    {
        var starts = new List<(int Start, int Type)>();
        for (var i = 0; i + 3 < data.Length; i++)
        {
            var sc = StartCode(data, i);
            if (sc == 0) continue;
            var header = i + sc;
            if (header < data.Length) starts.Add((i, data[header] & 0x1f));
            i = header;
        }

        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1].Start : data.Length;
            var len = end - starts[i].Start;
            var bytes = new byte[len];
            Buffer.BlockCopy(data, starts[i].Start, bytes, 0, len);
            yield return (starts[i].Type, bytes);
        }
    }

    private static int FindAud(List<byte> data, int start)
    {
        for (var i = Math.Max(0, start); i + 4 < data.Count; i++)
        {
            var sc = StartCode(data, i);
            if (sc > 0 && i + sc < data.Count && (data[i + sc] & 0x1f) == 9) return i;
        }
        return -1;
    }

    private static int StartCode(IReadOnlyList<byte> data, int i)
    {
        if (i + 2 < data.Count && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1) return 3;
        if (i + 3 < data.Count && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1) return 4;
        return 0;
    }

    private static int StartCode(ReadOnlySpan<byte> data, int i)
    {
        if (i + 2 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1) return 3;
        if (i + 3 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1) return 4;
        return 0;
    }
}
