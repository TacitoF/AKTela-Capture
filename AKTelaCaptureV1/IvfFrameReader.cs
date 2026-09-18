using System.Buffers.Binary;

namespace AKTelaCapture;

internal sealed class IvfFrameReader
{
    private readonly StreamingByteBuffer _buffer = new(512 * 1024);
    private bool _headerRead;

    public IEnumerable<(byte[] Data, bool Keyframe)> Push(byte[] bytes, int length)
    {
        _buffer.Append(bytes, length);
        var output = new List<(byte[], bool)>();

        if (!_headerRead)
        {
            if (_buffer.Length < 32) return output;
            var header = _buffer.Span;
            if (header[0] != (byte)'D' || header[1] != (byte)'K' || header[2] != (byte)'I' || header[3] != (byte)'F')
                throw new InvalidDataException("Cabeçalho IVF inválido.");
            _buffer.Consume(32);
            _headerRead = true;
        }

        while (_buffer.Length >= 12)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.Span[..4]);
            if (size > 8 * 1024 * 1024) throw new InvalidDataException("Frame VP8 inválido.");
            var frameSize = checked((int)size);
            var packetSize = checked(12 + frameSize);
            if (_buffer.Length < packetSize) break;

            var frame = _buffer.SliceToArray(12, frameSize);
            _buffer.Consume(packetSize);
            var keyframe = frame.Length > 0 && (frame[0] & 0x01) == 0;
            output.Add((frame, keyframe));
        }

        if (_buffer.Length > 10 * 1024 * 1024)
            _buffer.KeepTail(1024 * 1024);

        return output;
    }
}
