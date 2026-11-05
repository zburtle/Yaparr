using Yaparr.Voice;

namespace Yaparr.Tests;

// The leveller on made-up signals: a voice-like buzz in bursts at different loudness, and steady hiss.
public class SpeechLevelerTests
{
    private const int Rate = SpeechLeveler.SampleRate, Frame = 960;

    private static float[] Speech(int seconds, double level, int seed = 2, double f0 = 120)
    {
        var rng = new Random(seed);
        var x = new float[Rate * seconds];
        double phase = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double t = (double)i / Rate;
            phase += 2 * Math.PI * f0 * (1 + 0.15 * Math.Sin(2 * Math.PI * 0.7 * t)) / Rate;
            double env = Math.Pow(Math.Max(0, Math.Sin(2 * Math.PI * 1.7 * t + seed)), 0.6);
            double v = 0;
            for (int k = 1; k <= 24; k++) v += Math.Sin(k * phase) / k * (k < 8 ? 1.0 : 0.5);
            x[i] = (float)(level * env * v * 0.5 + 0.0003 * (rng.NextDouble() - 0.5));
        }
        return x;
    }

    private static float[] Hiss(int seconds, double sigma, int seed)
    {
        var rng = new Random(seed);
        var x = new float[Rate * seconds];
        for (int i = 0; i < x.Length; i++)
            x[i] = (float)(sigma * Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble()));
        return x;
    }

    private static double Db(double rms) => 20 * Math.Log10(Math.Max(rms, 1e-9));

    private static float[] Run(SpeechLeveler leveler, float[] input)
    {
        var o = (float[])input.Clone();
        for (int at = 0; at + Frame <= o.Length; at += Frame) leveler.Process(o.AsSpan(at, Frame));
        return o;
    }

    // The loudness of the stretches with speech in them, from a given second on: the RMS of the frames above a fixed threshold.
    private static double Active(float[] x, int fromSecond)
    {
        double sum = 0; int n = 0;
        for (int at = fromSecond * Rate; at + Frame <= x.Length; at += Frame)
        {
            double e = 0;
            for (int i = at; i < at + Frame; i++) e += x[i] * (double)x[i];
            e /= Frame;
            if (Db(Math.Sqrt(e)) > -45) { sum += e; n++; }
        }
        return Db(Math.Sqrt(sum / Math.Max(1, n)));
    }

    [Theory]
    [InlineData(0.05)]   // quiet microphone
    [InlineData(0.12)]
    [InlineData(0.3)]
    [InlineData(0.6)]    // loud one
    public void A_voice_ends_up_at_the_target_loudness(double level)
    {
        var leveler = new SpeechLeveler();
        var o = Run(leveler, Speech(30, level));
        Assert.InRange(Active(o, 20), leveler.TargetDbfs - 2, leveler.TargetDbfs + 2);
    }

    [Fact]
    public void Voices_of_very_different_loudness_come_within_a_few_decibels_of_each_other()
    {
        var before = new[] { 0.05, 0.12, 0.3, 0.6 }.Select(l => Active(Speech(30, l), 20)).ToArray();
        var after = new[] { 0.05, 0.12, 0.3, 0.6 }.Select(l => Active(Run(new SpeechLeveler(), Speech(30, l)), 20)).ToArray();
        Assert.True(before.Max() - before.Min() > 20, "the test voices should differ a lot");
        Assert.True(after.Max() - after.Min() < 3, $"spread after: {after.Max() - after.Min():0.0} dB");
    }

    [Fact]
    public void The_boost_and_the_cut_have_limits()
    {
        var leveler = new SpeechLeveler(maxBoostDb: 10, maxCutDb: 6);
        Run(leveler, Speech(30, 0.01));   // far too quiet to bring to the target with 10 dB
        Assert.True(leveler.GainDb <= 10.001f && leveler.GainDb > 9f, $"gain {leveler.GainDb}");
        leveler = new SpeechLeveler(maxBoostDb: 10, maxCutDb: 6);
        Run(leveler, Speech(30, 0.9));    // far too loud to bring to the target with 6 dB
        Assert.True(leveler.GainDb >= -6.001f && leveler.GainDb < -5f, $"gain {leveler.GainDb}");
    }

    [Fact]
    public void Background_noise_is_not_turned_up_in_the_pauses_or_on_its_own()
    {
        // Noise alone, from the start: never taken for speech, so never boosted.
        var leveler = new SpeechLeveler();
        var noise = Hiss(20, 0.003, 4);   // about -50 dBFS
        var o = Run(leveler, noise);
        Assert.Equal(0f, leveler.GainDb);
        Assert.Equal(noise, o);

        // After quiet talking, the gain is held while the noise goes on, not raised on it.
        leveler = new SpeechLeveler();
        var talk = Speech(15, 0.05);
        var all = talk.Concat(Hiss(20, 0.0015, 4)).ToArray();
        Run(leveler, talk);
        float afterTalk = leveler.GainDb;
        Assert.True(afterTalk > 5, "the quiet voice should have been raised");
        var o2 = Run(new SpeechLeveler(), all);
        var tail = all.AsSpan(15 * Rate).ToArray();
        var tailOut = o2.AsSpan(15 * Rate).ToArray();
        double rise = Db(Rms(tailOut)) - Db(Rms(tail));
        Assert.True(rise < afterTalk + 0.5, $"noise rose {rise:0.0} dB (gain after talking {afterTalk:0.0})");
    }

    private static double Rms(float[] x) { double s = 0; foreach (var v in x) s += v * (double)v; return Math.Sqrt(s / x.Length); }

    [Fact]
    public void The_gain_is_held_through_a_pause()
    {
        var leveler = new SpeechLeveler();
        Run(leveler, Speech(15, 0.05));
        float g = leveler.GainDb;
        Run(leveler, new float[5 * Rate]);   // silence
        Assert.Equal(g, leveler.GainDb);
    }

    [Fact]
    public void The_gain_moves_no_faster_than_its_rates()
    {
        var leveler = new SpeechLeveler();
        var x = Speech(20, 0.05).Concat(Speech(20, 0.9)).ToArray();
        float last = 0;
        for (int at = 0; at + Frame <= x.Length; at += Frame)
        {
            leveler.Process(x.AsSpan(at, Frame));
            float step = leveler.GainDb - last;
            double dt = (double)Frame / Rate;
            Assert.True(step <= leveler.RiseDbPerSecond * dt + 1e-4 && step >= -leveler.FallDbPerSecond * dt - 1e-4, $"step {step}");
            last = leveler.GainDb;
        }
    }

    [Fact]
    public void A_voice_already_at_the_target_is_left_alone()
    {
        // A level that gives about -24 dBFS: the gain should stay within a couple of dB of nothing.
        var probe = Active(Speech(30, 0.3), 20);
        double level = 0.3 * Math.Pow(10, (-24 - probe) / 20);
        var leveler = new SpeechLeveler();
        Run(leveler, Speech(30, level));
        Assert.InRange(leveler.GainDb, -2.0f, 2.0f);
    }

    [Fact]
    public void Peaks_never_pass_full_scale_and_odd_input_is_safe()
    {
        var leveler = new SpeechLeveler(targetDbfs: -3, maxBoostDb: 30);
        var o = Run(leveler, Speech(10, 0.5));
        Assert.All(o, s => Assert.InRange(s, -1f, 1f));

        var weird = new float[Frame];
        Array.Fill(weird, 5f);   // way past full scale
        leveler.Process(weird);
        Assert.All(weird, s => Assert.InRange(s, -1f, 1f));
        var empty = Array.Empty<float>();
        leveler.Process(empty);
        var nan = new float[Frame];
        Array.Fill(nan, float.NaN);
        leveler.Process(nan);   // must not poison the gain
        var after = Speech(1, 0.1).AsSpan(0, Frame).ToArray();
        leveler.Process(after);
        Assert.True(float.IsFinite(leveler.GainDb));
    }

    [Fact]
    public void Reset_forgets_the_voice()
    {
        var leveler = new SpeechLeveler();
        Run(leveler, Speech(15, 0.05));
        Assert.NotEqual(0f, leveler.GainDb);
        leveler.Reset();
        Assert.Equal(0f, leveler.GainDb);
    }
}
