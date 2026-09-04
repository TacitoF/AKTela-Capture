namespace AKTelaCapture;

internal sealed class H264AccessUnitReader
{
    private readonly List<byte> _buffer = new(512 * 1024);
    private byte[]? _sps;
    private byte[]? _pps;

    public List<(byte[] Data, bool KeyFrame)> Push(byte[] bytes, int length)
    {
        var output = new List<(byte[] Data, bool KeyFrame)>();
        for (var i = 0; i < length; i++) _buffer.Add(bytes[i]);

        while (true)
        {
            var firstAud = FindAud(_buffer, 0);
            if (firstAud < 0)
            {
                if (_buffer.Count > 4 * 1024 * 1024)
                    _buffer.RemoveRange(0, _buffer.Count - 1024 * 1024);
                break;
            }

            if (firstAud > 0) _buffer.RemoveRange(0, firstAud);
            var nextAud = FindAud(_buffer, 4);
            if (nextAud < 0) break;

            var unit = _buffer.GetRange(0, nextAud).ToArray();
            _buffer.RemoveRange(0, nextAud);
            CacheParameterSets(unit);
            var key = ContainsNalType(unit, 5);
            if (key && (!ContainsNalType(unit, 7) || !ContainsNalType(unit, 8)) && _sps is not null && _pps is not null)
                unit = PrependParameterSets(unit, _sps, _pps);
            output.Add((unit, key));
        }
        return output;
    }

    private void CacheParameterSets(byte[] unit)
    {
        foreach (var nal in EnumerateNals(unit))
        {
            if (nal.Type == 7) _sps = nal.Bytes;
            else if (nal.Type == 8) _pps = nal.Bytes;
        }
    }

    private static byte[] PrependParameterSets(byte[] unit, byte[] sps, byte[] pps)
    {
        var firstNal = EnumerateNals(unit).FirstOrDefault();
        var insertAt = firstNal.Type == 9 ? firstNal.Bytes.Length : 0;
        var result = new byte[unit.Length + sps.Length + pps.Length];
        Buffer.BlockCopy(unit, 0, result, 0, insertAt);
        Buffer.BlockCopy(sps, 0, result, insertAt, sps.Length);
        Buffer.BlockCopy(pps, 0, result, insertAt + sps.Length, pps.Length);
        Buffer.BlockCopy(unit, insertAt, result, insertAt + sps.Length + pps.Length, unit.Length - insertAt);
        return result;
    }

    private static int FindAud(List<byte> data, int start)
    {
        for (var i = Math.Max(0, start); i + 4 < data.Count; i++)
        {
            var sc = StartCodeLength(data, i);
            if (sc == 0) continue;
            var header = i + sc;
            if (header < data.Count && (data[header] & 0x1F) == 9) return i;
        }
        return -1;
    }

    private static bool ContainsNalType(byte[] data, int type) => EnumerateNals(data).Any(n => n.Type == type);

    private static IEnumerable<(int Type, byte[] Bytes)> EnumerateNals(byte[] data)
    {
        var starts = new List<(int Start, int Type)>();
        for (var i = 0; i + 3 < data.Length; i++)
        {
            var sc = StartCodeLength(data, i);
            if (sc == 0) continue;
            var header = i + sc;
            if (header < data.Length) starts.Add((i, data[header] & 0x1F));
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

    private static int StartCodeLength(List<byte> d, int i)
    {
        if (i + 2 < d.Count && d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1) return 3;
        if (i + 3 < d.Count && d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 0 && d[i + 3] == 1) return 4;
        return 0;
    }
    private static int StartCodeLength(byte[] d, int i)
    {
        if (i + 2 < d.Length && d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1) return 3;
        if (i + 3 < d.Length && d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 0 && d[i + 3] == 1) return 4;
        return 0;
    }
}
