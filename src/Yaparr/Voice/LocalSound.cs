namespace Yaparr.Voice;

/// <summary>
/// Sound put in by hand, for the sharer who plays a film in the room and hears it too: the decoded sound is pushed here as it is sent, and the speakers pull it out.
/// The film is paced by one clock and the sound card consumes by another, so the two drift apart a little; the sound is played a hair faster or slower (a fraction of a percent,
/// which cannot be heard) to keep what is held at the level wanted, instead of letting it run dry or cutting some of it out, either of which clicks. Silence is played when it does
/// run dry, with a short fade at each edge.
/// </summary>
public sealed class LocalSound
{
    private const int Channels = StreamAudioSender.Channels;
    private readonly float[] _ring = new float[48000 * Channels * 2];
    private int _start, _count;
    private double _pos;   // how far into the first frame held the next one to play is (a part of a frame, between frames)
    private readonly object _lock = new();
    private volatile float _gain = 1f;
    private volatile int _delayMs;
    private volatile int _maxDelayMs;
    private int _targetMs;
    private long _cleanFrames;   // frames played since it last ran dry or was eased down
    private bool _primed;
    private float _ramp = 1f;
    private int _underruns, _trims;

    // A gap in the sound would click if it began or ended on a loud sample: what plays just before a gap fades out, and what plays after one fades in.
    private const int FadeOutFrames = 96, FadeInFrames = 240;   // 2 ms and 5 ms at 48 kHz

    // How much faster or slower it is played, at most, when what is held is a hundred milliseconds off what is wanted.
    private const double MaxSpeedChange = 0.004;

    /// <summary>
    /// How much is held back before it plays, which is what smooths out the pieces arriving unevenly. It takes effect when the sound starts (or starts again after running dry);
    /// afterwards the speed of playing is eased so that the level held comes back to it.
    /// </summary>
    public int DelayMs { get => _delayMs; set { _delayMs = Math.Clamp(value, 0, 1500); _targetMs = Math.Max(_targetMs, _delayMs); } }

    /// <summary>
    /// When above <see cref="DelayMs"/>, what is held back adapts: it is DelayMs while the sound keeps up, and each time it runs dry it is raised (by 20 ms, up to this) so it does not
    /// again; after half a minute without running dry it is eased back down. The delay is then only as long as this computer and the film's pieces make necessary.
    /// </summary>
    public int MaxDelayMs { get => _maxDelayMs; set => _maxDelayMs = Math.Clamp(value, 0, 1500); }

    /// <summary>What is held back now, in milliseconds (between DelayMs and MaxDelayMs).</summary>
    public int TargetMs { get { lock (_lock) return Math.Max(_targetMs, _delayMs); } }

    /// <summary>How loud it is, 0 to 2 (1 is as decoded).</summary>
    public float Gain { get => _gain; set => _gain = Math.Clamp(value, 0f, 2f); }

    /// <summary>How much sound is held now, in milliseconds.</summary>
    public int BufferedMs { get { lock (_lock) return (int)(((_count / Channels) - _pos) / 48.0); } }

    /// <summary>How many times it ran dry (the pieces came too late to keep up).</summary>
    public int Underruns => Volatile.Read(ref _underruns);

    /// <summary>How many times far too much had built up and some had to be cut out.</summary>
    public int Trims => Volatile.Read(ref _trims);

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
            int target = Math.Clamp(_targetMs, _delayMs, Math.Max(_delayMs, _maxDelayMs));
            _targetMs = target;
            int wanted = target * 48 * Channels;
            if (_count == 0) _primed = false;
            else if (!_primed && _count >= wanted) { _primed = true; _ramp = 0f; _pos = 0; }
            if (!_primed) { output.Clear(); return; }

            int heldFrames = _count / Channels, wantedFrames = wanted / Channels;
            // Far more is held than wanted (the player stalled and caught up): some is cut out, 10 ms each time.
            if (heldFrames - wantedFrames > 19_200)
            {
                int drop = Math.Min(480, heldFrames - wantedFrames) * Channels;
                _start = (_start + drop) % _ring.Length;
                _count -= drop;
                heldFrames -= drop / Channels;
                Interlocked.Increment(ref _trims);
            }
            // A little more held than wanted: play a little faster, to come back to it; a little less: a little slower.
            double error = Math.Clamp((heldFrames - wantedFrames) / 4800.0, -1.0, 1.0);
            double step = 1.0 + error * MaxSpeedChange;

            int outFrames = output.Length / Channels, made = 0;
            for (; made < outFrames; made++)
            {
                int at = (int)_pos;
                if ((at + 2) * Channels > _count) break;   // the frame after this one is needed to play between the two
                float part = (float)(_pos - at);
                if (_ramp < 1f) _ramp = Math.Min(1f, _ramp + 1f / FadeInFrames);
                for (int c = 0; c < Channels; c++)
                {
                    float a = _ring[(_start + at * Channels + c) % _ring.Length], b = _ring[(_start + (at + 1) * Channels + c) % _ring.Length];
                    float s = (a + (b - a) * part) * gain * _ramp;
                    output[made * Channels + c] = s > 1f ? 1f : s < -1f ? -1f : s;
                }
                _pos += step;
            }
            if (_maxDelayMs > _delayMs)
            {
                _cleanFrames += made;
                if (_cleanFrames > 48000L * 30 && _targetMs > _delayMs) { _targetMs = Math.Max(_delayMs, _targetMs - 10); _cleanFrames = 0; }
            }
            int used = (int)_pos;
            _start = (_start + used * Channels) % _ring.Length;
            _count -= used * Channels;
            _pos -= used;

            if (made < outFrames)
            {
                // It ran dry: the end of what there was fades out, so the silence that follows starts without a click, and it waits until enough is held again.
                int tail = Math.Min(made, FadeOutFrames);
                for (int j = 0; j < tail; j++)
                {
                    float k = (tail - 1 - j) / (float)tail;
                    for (int c = 0; c < Channels; c++) output[(made - tail + j) * Channels + c] *= k;
                }
                output[(made * Channels)..].Clear();
                _primed = false;
                _pos = 0;
                Interlocked.Increment(ref _underruns);
                _cleanFrames = 0;
                if (_maxDelayMs > _delayMs) _targetMs = Math.Min(_maxDelayMs, _targetMs + 20);
            }
        }
    }
}
