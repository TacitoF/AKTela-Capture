namespace AKTelaCapture;

// Encoded delta frames depend on earlier frames. After overflow, resume only at an IDR.
internal sealed class VideoPacketQueue(int capacity)
{
    private readonly Queue<byte[]> _packets = new();
    private readonly object _gate = new();
    private bool _waitingForKeyframe = true;
    public long Dropped { get; private set; }
    public int Count { get { lock (_gate) return _packets.Count; } }

    public bool TryWrite(byte[] packet)
    {
        lock (_gate)
        {
            var keyframe = PacketProtocol.IsKeyframe(packet);
            if (keyframe)
            {
                Dropped += _packets.Count;
                _packets.Clear();
                _waitingForKeyframe = false;
            }
            else if (_waitingForKeyframe || _packets.Count >= capacity)
            {
                Dropped += _packets.Count + 1;
                _packets.Clear();
                _waitingForKeyframe = true;
                return false;
            }
            _packets.Enqueue(packet);
            return true;
        }
    }

    public bool TryRead(out byte[] packet)
    {
        lock (_gate) return _packets.TryDequeue(out packet!);
    }

    public void Reset()
    {
        lock (_gate)
        {
            Dropped += _packets.Count;
            _packets.Clear();
            _waitingForKeyframe = true;
        }
    }
}
