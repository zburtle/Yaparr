namespace Yaparr.Transport;

internal sealed class ReliableChannel
{
    public const int MaxSegment = 1000;
    private const int Window = 64;
    private const int MaxQueuedSegments = 8192;
    private const int MaxReorder = 1024;

    /// <summary>
    /// The most a single message may hold while it is being put together, and the most that waits out of order. Anyone can
    /// open a connection, so what a peer is allowed to make us hold has to be bounded here.
    /// </summary>
    internal const int MaxMessageBytes = 4 * 1024 * 1024;
    private const int MaxReorderBytes = 1024 * 1024;
    private const long BaseRtoMs = 300;
    private const long MaxRtoMs = 3000;

    private sealed class InFlight
    {
        public required byte[] Payload;
        public bool Last;
        public long SentAt;
        public long FirstSentAt;
        public int Retries;
    }

    private readonly Queue<(byte[] Payload, bool Last)> _queue = new();
    private readonly SortedDictionary<uint, InFlight> _inFlight = new();
    private uint _nextSeq;

    private uint _expected;
    private readonly Dictionary<uint, (byte[] Payload, bool Last)> _reorder = new();
    private readonly List<byte[]> _assembling = new();
    private int _reorderBytes, _assemblingBytes;

    public uint Expected => _expected;

    public void Enqueue(ReadOnlySpan<byte> message)
    {
        int segments = Math.Max(1, (message.Length + MaxSegment - 1) / MaxSegment);
        if (_queue.Count + segments > MaxQueuedSegments)
            throw new InvalidOperationException("Reliable send queue is full.");

        for (int i = 0; i < segments; i++)
        {
            var chunk = message.Slice(i * MaxSegment, Math.Min(MaxSegment, message.Length - i * MaxSegment));
            _queue.Enqueue((chunk.ToArray(), i == segments - 1));
        }
    }

    public void Pump(long now, Action<uint, bool, byte[]> transmit)
    {
        while (_inFlight.Count < Window && _queue.Count > 0)
        {
            var (payload, last) = _queue.Dequeue();
            uint seq = _nextSeq++;
            _inFlight[seq] = new InFlight { Payload = payload, Last = last, SentAt = now, FirstSentAt = now };
            transmit(seq, last, payload);
        }

        foreach (var (seq, f) in _inFlight)
        {
            long rto = Math.Min(MaxRtoMs, BaseRtoMs << Math.Min(f.Retries, 4));
            if (now - f.SentAt < rto) continue;
            f.SentAt = now;
            f.Retries++;
            transmit(seq, f.Last, f.Payload);
        }
    }

    public long OldestUnackedAge(long now) => _inFlight.Count == 0 ? 0 : now - _inFlight.Values.Min(f => f.FirstSentAt);

    public void OnAck(uint next)
    {
        foreach (var seq in _inFlight.Keys.Where(s => (int)(s - next) < 0).ToList())
            _inFlight.Remove(seq);
    }

    /// <summary>False when the peer broke a limit; the connection should be closed.</summary>
    public bool OnSegment(uint seq, bool last, byte[] payload, List<byte[]> delivered)
    {
        int ahead = (int)(seq - _expected);
        if (ahead < 0 || ahead >= MaxReorder || _reorder.ContainsKey(seq)) return true;
        if (ahead > 0 && _reorderBytes + payload.Length > MaxReorderBytes) return true;
        _reorder[seq] = (payload, last);
        _reorderBytes += payload.Length;

        while (_reorder.Remove(_expected, out var seg))
        {
            _expected++;
            _reorderBytes = Math.Max(0, _reorderBytes - seg.Payload.Length);
            _assemblingBytes += seg.Payload.Length;
            if (_assemblingBytes > MaxMessageBytes)
            {
                _assembling.Clear();
                _assemblingBytes = 0;
                _reorder.Clear();
                _reorderBytes = 0;
                return false;
            }
            _assembling.Add(seg.Payload);
            if (!seg.Last) continue;

            delivered.Add(_assembling.Count == 1 ? _assembling[0] : _assembling.SelectMany(a => a).ToArray());
            _assembling.Clear();
            _assemblingBytes = 0;
        }
        return true;
    }
}
