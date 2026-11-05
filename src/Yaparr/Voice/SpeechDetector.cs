namespace Yaparr.Voice;

/// <summary>
/// Tells frames with speech in them from the rest, for one voice: a frame is speech when it is well above the noise floor, which
/// is followed from the quietest frames (down at once, up only slowly, so a long stretch of talking does not become the floor),
/// and above a fixed minimum. It also reports the frame's loudness. Mono, 48 kHz, samples from -1 to 1. Not thread safe.
/// </summary>
public sealed class SpeechDetector
{
    public const int SampleRate = 48000;

    /// <summary>The loudness reported for a frame of digital silence, in dB below full scale.</summary>
    public const float SilenceDb = -100f;

    private const float FloorDb = -80f, MinSpeechDb = -55f, SpeechAboveFloorDb = 10f;
    private const float FloorRiseDbPerSecond = 1.5f;

    // The first frame is the first guess, which errs on the side of calling nothing speech until a quieter frame has been seen.
    private float _floorDb = float.NaN;

    /// <summary>How loud the quietest stretches have been lately, in dB below full scale (NaN before the first frame).</summary>
    public float NoiseFloorDb => _floorDb;

    public void Reset() => _floorDb = float.NaN;

    /// <summary>Looks at one frame: returns its loudness (RMS in dB relative to full scale) and says whether it has speech in it.</summary>
    public float Update(ReadOnlySpan<float> frame, out bool speech)
    {
        speech = false;
        if (frame.IsEmpty) return SilenceDb;
        double dt = (double)frame.Length / SampleRate;

        double sum = 0;
        foreach (var s in frame) sum += s * (double)s;
        float rmsDb = sum <= 0 ? SilenceDb : (float)(10 * Math.Log10(sum / frame.Length + 1e-12));
        if (float.IsNaN(rmsDb)) rmsDb = SilenceDb;

        if (float.IsNaN(_floorDb)) _floorDb = Math.Max(FloorDb, rmsDb);
        if (rmsDb < _floorDb) _floorDb = Math.Max(FloorDb, rmsDb);
        else _floorDb = Math.Min(_floorDb + (float)(FloorRiseDbPerSecond * dt), rmsDb);

        speech = rmsDb > Math.Max(MinSpeechDb, _floorDb + SpeechAboveFloorDb);
        return rmsDb;
    }
}
