namespace Yaparr.Voice;

/// <summary>
/// Brings one person's voice to a steady loudness: a slow gain that follows how loud they talk, so a quiet microphone is raised
/// and a loud one lowered, whoever it is and wherever the audio is processed (on the sender's microphone, or on each person heard).
/// It is made for open microphones, where an automatic volume tends to make the room's noise swell between words, so:
/// <list type="bullet">
/// <item>the gain moves only while someone is talking (a frame counts as talking when it is well above the noise floor, which is
/// followed from the quietest frames) and is held through pauses, so the gain is never raised on noise (the gain it holds still applies to the noise, as it does to the voice);</item>
/// <item>it rises slowly and falls fast (12 dB a second up, 40 down, like TeamTalk's defaults), and is limited to a few dB of
/// boost and cut, so it cannot be driven far by one odd stretch;</item>
/// <item>a soft limiter catches peaks the gain brings past full scale.</item>
/// </list>
/// Mono, 48 kHz, samples from -1 to 1, in frames of any size (20 ms is what the voice session uses). Not thread safe.
/// </summary>
public sealed class SpeechLeveler
{
    public const int SampleRate = 48000;

    /// <summary>The loudness to aim for: the RMS of the frames with speech in them, in dB below full scale.</summary>
    public float TargetDbfs { get; }
    public float MaxBoostDb { get; }
    public float MaxCutDb { get; }
    public float RiseDbPerSecond { get; }
    public float FallDbPerSecond { get; }

    private const float FloorDb = -80f, SilenceDb = -100f;
    private const float MinSpeechDb = -55f, SpeechAboveFloorDb = 10f;
    private const float FloorRiseDbPerSecond = 1.5f;

    private float _gainDb;
    private float _levelDb = float.NaN;     // how loud they talk, smoothed over the frames with speech in them
    private float _floorDb = float.NaN;     // how loud the quietest stretches are; the first frame is the first guess, which errs on the side of not boosting

    public SpeechLeveler(float targetDbfs = -26f, float maxBoostDb = 12f, float maxCutDb = 15f, float riseDbPerSecond = 12f, float fallDbPerSecond = 40f)
    {
        TargetDbfs = targetDbfs;
        MaxBoostDb = maxBoostDb;
        MaxCutDb = maxCutDb;
        RiseDbPerSecond = riseDbPerSecond;
        FallDbPerSecond = fallDbPerSecond;
    }

    /// <summary>The gain being applied now, in dB (negative: the voice is being turned down).</summary>
    public float GainDb => _gainDb;

    /// <summary>Whether the last frame was taken for speech.</summary>
    public bool Talking { get; private set; }

    /// <summary>Forgets what was learned about this voice: the next speech starts again from no change.</summary>
    public void Reset()
    {
        _gainDb = 0;
        _levelDb = float.NaN;
        _floorDb = float.NaN;
        Talking = false;
    }

    /// <summary>Changes the loudness of one frame in place.</summary>
    public void Process(Span<float> frame)
    {
        if (frame.IsEmpty) return;
        double dt = (double)frame.Length / SampleRate;

        double sum = 0;
        foreach (var s in frame) sum += s * (double)s;
        float rmsDb = sum <= 0 ? SilenceDb : (float)(10 * Math.Log10(sum / frame.Length + 1e-12));
        if (float.IsNaN(rmsDb)) rmsDb = SilenceDb;

        // The noise floor: down at once to a quieter frame, up only slowly, so a long stretch of talking does not become the floor.
        if (float.IsNaN(_floorDb)) _floorDb = Math.Max(FloorDb, rmsDb);
        if (rmsDb < _floorDb) _floorDb = Math.Max(FloorDb, rmsDb);
        else _floorDb = Math.Min(_floorDb + (float)(FloorRiseDbPerSecond * dt), rmsDb);

        bool talking = rmsDb > Math.Max(MinSpeechDb, _floorDb + SpeechAboveFloorDb);
        Talking = talking;
        float before = _gainDb;

        if (talking)
        {
            // Quick to notice that they got louder, slow to notice that they got quieter.
            if (float.IsNaN(_levelDb)) _levelDb = rmsDb;
            else
            {
                double tau = rmsDb > _levelDb ? 0.15 : 1.5;
                _levelDb += (float)((rmsDb - _levelDb) * (1 - Math.Exp(-dt / tau)));
            }
            float wanted = Math.Clamp(TargetDbfs - _levelDb, -MaxCutDb, MaxBoostDb);
            if (wanted > _gainDb) _gainDb = Math.Min(wanted, _gainDb + (float)(RiseDbPerSecond * dt));
            else _gainDb = Math.Max(wanted, _gainDb - (float)(FallDbPerSecond * dt));
        }

        // The gain moves smoothly across the frame, so there is no step at its edge.
        float from = DbToLinear(before), to = DbToLinear(_gainDb);
        int n = frame.Length;
        for (int i = 0; i < n; i++)
        {
            float g = from + (to - from) * (i + 1) / n;
            frame[i] = Limit(frame[i] * g);
        }
    }

    private static float DbToLinear(float db) => MathF.Pow(10f, db / 20f);

    // Up to 0.9 untouched; beyond it bent smoothly towards 1, which sounds less harsh than cutting the peak off.
    private static float Limit(float s)
    {
        const float knee = 0.9f, room = 1f - knee;
        float a = MathF.Abs(s);
        if (a <= knee) return s;
        float limited = knee + room * MathF.Tanh((a - knee) / room);
        return s < 0 ? -limited : limited;
    }
}
