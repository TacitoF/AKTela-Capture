using System.Buffers.Binary;

namespace AKTelaCapture;

internal sealed class IvfFrameReader
{
    private readonly List<byte> _buffer = new(512 * 1024);
    private bool _headerRead;

    public IEnumerable<(byte[] Data, bool Keyframe)> Push(byte[] bytes, int length)
    {
        for (var i = 0; i < length; i++) _buffer.Add(bytes[i]);
        var output = new List<(byte[], bool)>();

        if (!_headerRead)
        {
            if (_buffer.Count < 32) return output;
            if (_buffer[0] != (byte)'D' || _buffer[1] != (byte)'K' || _buffer[2] != (byte)'I' || _buffer[3] != (byte)'F')
                throw new InvalidDataException("Cabeçalho IVF inválido.");
            _buffer.RemoveRange(0, 32);
            _headerRead = true;
        }

        while (_buffer.Count >= 12)
        {
            Span<byte> header = stackalloc byte[12];
            for (var i = 0; i < 12; i++) header[i] = _buffer[i];
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
            if (size > 8 * 1024 * 1024) throw new InvalidDataException("Frame VP8 inválido.");
            if (_buffer.Count < 12 + size) break;

            var frame = _buffer.GetRange(12, checked((int)size)).ToArray();
            _buffer.RemoveRange(0, 12 + checked((int)size));
            var key = frame.Length > 0 && (frame[0] & 0x01) == 0;
            output.Add((frame, key));
        }

        if (_buffer.Count > 10 * 1024 * 1024)
            _buffer.RemoveRange(0, _buffer.Count - 1024 * 1024);

        return output;
    }
}
