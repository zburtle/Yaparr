namespace Yaparr.Voice;

/// <summary>
/// Sound put in by hand, for the sharer who plays a film in the room and hears it too: the decoded sound is pushed here as it is sent, and the speakers pull it out.
/// A little over a second is kept; what does not fit is dropped (the oldest first), and silence is played when it runs dry.
/// </summary>
public sealed class LocalSound
{
    private readonly float[] _ring = new float[48000 * StreamAudioSender.Channels * 2];
    private int _start, _count;
    private readonly object _lock = new();
    private volatile float _gain = 1f;
    private volatile int _delayMs;
    private bool _primed;

    /// <summary>
    /// How far behind it is held, so that it stays with a picture that is itself shown late (the viewer's smoothing queue and the decoder). It takes effect when the sound starts
    /// (or starts again after running dry); if it falls later, the surplus is caught up on gently, a little each time it is pulled.
    /// </summary>
    public int DelayMs { get => _delayMs; set => _delayMs = Math.Clamp(value, 0, 1500); }

    /// <summary>How loud it is, 0 to 2 (1 is as decoded).</summary>
    public float Gain { get => _gain; set => _gain = Math.Clamp(value, 0f, 2f); }

    /// <summary>Interleaved stereo samples at 48 kHz.</summary>
    public void Push(ReadOnlySpan<float> samples)
    {
        lock (_lock)
        {
            if (samples.Length > _ring.Length) samples = samples[^_ring.Length..];
            int over = _count + samples.Length - _ring.Length;
            if (over > 0) { _start = (_start + over) % _ring.Length; _count -= over; }
            int end = (_start + _count) % _ring.Length;
            int first = Math.Min(samples.Length, _ring.Length - end);
            samples[..first].CopyTo(_ring.AsSpan(end));
            samples[first..].CopyTo(_ring);
            _count += samples.Length;
        }
    }

    /// <summary>Fills <paramref name="output"/> with interleaved stereo samples; silence for what is not there.</summary>
    public void Pull(Span<float> output)
    {
        float gain = _gain;
        lock (_lock)
        {
            int wanted = _delayMs * 48 * StreamAudioSender.Channels;
            if (_count == 0) _primed = false;
            else if (!_primed && _count >= wanted) _primed = true;
            if (!_primed) { output.Clear(); return; }
            // More is held than wanted by over 100 ms (the delay fell): drop 10 ms of it each time.
            if (_count - wanted > 4800 * StreamAudioSender.Channels)
            {
                int drop = Math.Min(480 * StreamAudioSender.Channels, _count - wanted);
                _start = (_start + drop) % _ring.Length;
                _count -= drop;
            }
            int n = Math.Min(output.Length, _count);
            for (int i = 0; i < n; i++)
            {
                float s = _ring[(_start + i) % _ring.Length] * gain;
                output[i] = s > 1f ? 1f : s < -1f ? -1f : s;
            }
            _start = (_start + n) % _ring.Length;
            _count -= n;
            output[n..].Clear();
        }
    }
}
