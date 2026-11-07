namespace Yaparr.Voice;

/// <summary>
/// Takes short, sharp clicks out of the microphone: keyboard keys, mouse buttons and the like. Steady noise is what
/// <c>WebRtcVoiceProcessor</c> removes, and a click is not steady, so it gets through. This works on 1 ms blocks:
/// a block is loud when it is well above a followed noise floor, and a run of loud blocks is a click only when it
/// starts sharply, is no longer than <see cref="MaxClickBlocks"/>, and ends again. Such a run is turned down by
/// <see cref="AttenuationDb"/>. Anything longer (speech, a cough) is left alone.
/// <para>
/// To know whether a run ends in time, the output lags the input by 10 ms (<see cref="DelaySamples"/>); the voice session
/// adds that delay only while this is switched on. Mono, 48 kHz, samples from -1 to 1, in frames that are a multiple of
/// 1 ms (48 samples). Not thread safe: it is used by the microphone's thread only.
/// </para>
/// </summary>
public sealed class ClickSuppressor
{
    public const int SampleRate = 48000;
    public const int BlockSamples = 48;                                 // 1 ms
    public const int DelaySamples = 10 * BlockSamples;                  // 10 ms of lookahead
    private const int LookaheadBlocks = 9;                              // a block is decided when this many blocks after it are in
    public const int MaxClickBlocks = 8;                                // the longest loud run taken for a click (8 ms)

    public const float ThresholdDb = 15f;                               // how far above the noise floor a loud block is
    public const float OnsetDb = 10f;                                   // how much louder the first block of a click is than the one before
    public const float AttenuationDb = -30f;
    private const float FloorRiseDbPerBlock = 0.002f;                   // the floor follows quiet blocks up slowly and down at once
    private const float FloorMinDb = -70f;

    private const int BlockRing = 64, SampleRing = 8192;

    private readonly float[] _samples = new float[SampleRing];
    private readonly double[] _db = new double[BlockRing];
    private readonly bool[] _loud = new bool[BlockRing];
    private readonly float[] _target = Enumerable.Repeat(1f, BlockRing).ToArray();   // every block passes until it is decided
    private readonly float _attenuation = (float)Math.Pow(10, AttenuationDb / 20);
    private long _count;                                                // samples written so far
    private double _floorDb = double.NaN;

    /// <summary>Cleans a frame in place. The frame comes out <see cref="DelaySamples"/> later than it went in (silence at first).</summary>
    public void Process(Span<float> frame)
    {
        if (frame.Length % BlockSamples != 0) throw new ArgumentException($"A frame must be a multiple of {BlockSamples} samples (1 ms).", nameof(frame));
        for (int i = 0; i < frame.Length; i++)
        {
            long t = _count++;
            _samples[t % SampleRing] = frame[i];
            if (t >= DelaySamples)
            {
                long s = t - DelaySamples;
                // The gain of a block is in place from its first sample: the click's onset is the loudest part, so a slow
                // turn-down would let most of it through. Blocks next to a click are quiet, so the step is not heard.
                frame[i] = _samples[s % SampleRing] * _target[(s / BlockSamples) % BlockRing];
            }
            else frame[i] = 0f;

            if ((t + 1) % BlockSamples == 0)
            {
                long newest = t / BlockSamples;
                Analyze(newest);
                if (newest >= LookaheadBlocks) Decide(newest - LookaheadBlocks);
            }
        }
    }

    // Loudness of one block, and whether it is loud against the floor. The floor is only moved by quiet blocks.
    private void Analyze(long block)
    {
        double sum = 0;
        long first = block * BlockSamples;
        for (int j = 0; j < BlockSamples; j++)
        {
            double x = _samples[(first + j) % SampleRing];
            sum += x * x;
        }
        double db = 10 * Math.Log10(sum / BlockSamples + 1e-12);
        if (double.IsNaN(_floorDb)) _floorDb = db;
        bool loud = db > _floorDb + ThresholdDb;
        if (!loud)
            _floorDb = db < _floorDb ? Math.Max(db, FloorMinDb) : _floorDb + FloorRiseDbPerBlock;

        int k = (int)(block % BlockRing);
        _db[k] = db;
        _loud[k] = loud;
    }

    private bool Loud(long block) => _loud[block % BlockRing];

    // Decides the gain of one block, once the blocks after it are known.
    private void Decide(long block)
    {
        long newest = block + LookaheadBlocks;
        _target[block % BlockRing] = IsClick(block, newest) ? _attenuation : 1f;
    }

    private bool IsClick(long block, long newest)
    {
        if (!Loud(block)) return false;

        long start = block;
        while (block - start < MaxClickBlocks && start - 1 >= 1 && Loud(start - 1)) start--;

        long end = block;
        while (end < newest && Loud(end + 1)) end++;
        if (end >= newest) return false;                    // still loud at the newest block: too long to be a click
        if (end - start + 1 > MaxClickBlocks) return false;
        if (start < 1) return false;

        return _db[start % BlockRing] - _db[(start - 1) % BlockRing] >= OnsetDb;
    }
}
