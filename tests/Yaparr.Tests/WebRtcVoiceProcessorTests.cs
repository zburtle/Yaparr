using Yaparr.Voice;
using Yaparr.Voice.WebRtc;

namespace Yaparr.Tests;

// The real WebRTC processing, fed the way the app feeds it: every 20 ms the speaker blocks (as the sound card asks for
// them, 10 ms at a time) and then one microphone frame. The signals are made up: a voice-like buzz in bursts, a room
// that returns the far end half a loudness and 80 ms later, and steady hiss. The limits are well inside what was measured.
public class WebRtcVoiceProcessorTests
{
    private const int Rate = 48000, Frame = VoiceSession.FrameSamples, Block = 480;

    private static float[] Speech(int seconds, double f0Base, int seed, double level)
    {
        var rng = new Random(seed);
        var x = new float[Rate * seconds];
        double phase = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double t = (double)i / Rate;
            phase += 2 * Math.PI * f0Base * (1 + 0.15 * Math.Sin(2 * Math.PI * 0.7 * t)) / Rate;
            double env = Math.Pow(Math.Max(0, Math.Sin(2 * Math.PI * 1.7 * t + seed)), 0.6);
            double v = 0;
            for (int k = 1; k <= 24; k++) v += Math.Sin(k * phase) / k * (k < 8 ? 1.0 : 0.5);
            x[i] = (float)(level * env * v * 0.5 + 0.003 * (rng.NextDouble() - 0.5) * env);
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

    private static float[] Room(float[] far)
    {
        var y = new float[far.Length];
        for (int i = 0; i < y.Length; i++)
        {
            if (i >= 80 * 48) y[i] += 0.5f * far[i - 80 * 48];
            if (i >= 95 * 48) y[i] += 0.2f * far[i - 95 * 48];
        }
        return y;
    }

    private static float[] Mix(params float[][] parts)
    {
        var sum = new float[parts[0].Length];
        foreach (var p in parts) for (int i = 0; i < sum.Length; i++) sum[i] += p[i];
        return sum;
    }

    private static double Rms(float[] x, int from, int to)
    {
        double s = 0;
        for (int i = from; i < to; i++) s += x[i] * (double)x[i];
        return Math.Sqrt(s / (to - from));
    }

    private static double Db(double ratio) => 20 * Math.Log10(Math.Max(ratio, 1e-9));

    private static WebRtcVoiceProcessor Make(VoiceProcessing settings)
    {
        Assert.True(WebRtcVoiceProcessor.TryCreate(settings, out var processor, out var error), error);
        return processor!;
    }

    private static float[] Run(IVoiceProcessor processor, float[] far, float[] mic)
    {
        var output = new float[mic.Length];
        var frame = new float[Frame];
        for (int at = 0; at + Frame <= mic.Length; at += Frame)
        {
            processor.ProcessPlayback(far.AsSpan(at, Block));
            processor.ProcessPlayback(far.AsSpan(at + Block, Block));
            mic.AsSpan(at, Frame).CopyTo(frame);
            processor.ProcessCapture(frame);
            frame.CopyTo(output, at);
        }
        return output;
    }

    [Fact]
    public void The_library_loads_on_this_system()
    {
        Assert.True(WebRtcVoiceProcessor.IsAvailable(out var reason), reason);
    }

    [Fact]
    public void Echo_of_the_other_side_is_removed_once_it_has_settled()
    {
        var far = Speech(20, 130, 1, 0.4);
        var mic = Room(far);
        using var processor = Make(new VoiceProcessing(EchoCancellation: true, Noise: NoiseSuppression.Off));
        var o = Run(processor, far, mic);
        double left = Db(Rms(o, 14 * Rate, 20 * Rate) / Rms(mic, 14 * Rate, 20 * Rate));
        Assert.True(left < -25, $"echo only came down {-left:0} dB");
    }

    [Fact]
    public void Echo_cancellation_works_with_the_microphone_and_speakers_on_different_threads()
    {
        var far = Speech(20, 130, 1, 0.4);
        var mic = Room(far);
        using var processor = Make(new VoiceProcessing(EchoCancellation: true, Noise: NoiseSuppression.Off));
        var o = new float[mic.Length];
        int frames = mic.Length / Frame, played = 0, captured = 0;

        var speaker = new Thread(() =>
        {
            for (int f = 0; f < frames; f++)
            {
                while (f - Volatile.Read(ref captured) > 6) Thread.SpinWait(50); // the speaker runs a little ahead, as in real life
                processor.ProcessPlayback(far.AsSpan(f * Frame, Block));
                processor.ProcessPlayback(far.AsSpan(f * Frame + Block, Block));
                Interlocked.Increment(ref played);
            }
        });
        var microphone = new Thread(() =>
        {
            var frame = new float[Frame];
            for (int f = 0; f < frames; f++)
            {
                while (Volatile.Read(ref played) <= f) Thread.SpinWait(50);
                mic.AsSpan(f * Frame, Frame).CopyTo(frame);
                processor.ProcessCapture(frame);
                frame.CopyTo(o, f * Frame);
                Interlocked.Increment(ref captured);
            }
        });
        speaker.Start(); microphone.Start(); speaker.Join(); microphone.Join();

        double left = Db(Rms(o, 14 * Rate, 20 * Rate) / Rms(mic, 14 * Rate, 20 * Rate));
        Assert.True(left < -25, $"echo only came down {-left:0} dB");
    }

    [Fact]
    public void Steady_background_noise_is_reduced_and_speech_is_kept()
    {
        var voice = Speech(12, 120, 2, 0.3);
        var mic = Mix(voice, Hiss(12, 0.02, 5));
        using var processor = Make(new VoiceProcessing(EchoCancellation: false, Noise: NoiseSuppression.Moderate));
        var o = Run(processor, new float[mic.Length], mic);

        double quietIn = 0, quietOut = 0, talkIn = 0, talkOut = 0; int quiet = 0, talk = 0;
        for (int at = 300 * Frame / 2; at + Frame <= mic.Length; at += Frame)
        {
            double level = Rms(voice, at, at + Frame);
            if (level < 1e-4) { quietIn += Math.Pow(Rms(mic, at, at + Frame), 2); quietOut += Math.Pow(Rms(o, at, at + Frame), 2); quiet++; }
            else if (level > 0.05) { talkIn += Math.Pow(Rms(mic, at, at + Frame), 2); talkOut += Math.Pow(Rms(o, at, at + Frame), 2); talk++; }
        }
        Assert.True(quiet > 20 && talk > 20);
        Assert.True(Db(Math.Sqrt(quietOut / quiet) / Math.Sqrt(quietIn / quiet)) < -7, "background noise was not reduced enough");
        Assert.True(Db(Math.Sqrt(talkOut / talk) / Math.Sqrt(talkIn / talk)) > -4, "speech was cut too much");
    }

    [Fact]
    public void Your_voice_survives_the_other_side_talking_at_the_same_time()
    {
        var far = Speech(20, 130, 1, 0.4);
        var near = Speech(20, 210, 7, 0.3);
        var echo = Room(far);
        var settings = new VoiceProcessing(EchoCancellation: true, Noise: NoiseSuppression.Moderate);

        using var both = Make(settings);
        var together = Run(both, far, Mix(echo, near));
        using var alone = Make(settings);
        var you = Run(alone, new float[far.Length], near);
        using var other = Make(settings);
        var leftover = Run(other, far, echo);

        int a = 14 * Rate, b = 20 * Rate;
        Assert.InRange(Db(Rms(together, a, b) / Rms(you, a, b)), -3, 3);
        Assert.True(Db(Rms(leftover, a, b) / Rms(you, a, b)) < -25, "the echo was not removed");
    }

    [Fact]
    public void With_everything_off_the_sound_passes_through()
    {
        var tone = new float[Rate * 2];
        for (int i = 0; i < tone.Length; i++) tone[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 1000 * i / Rate));
        using var processor = Make(new VoiceProcessing(EchoCancellation: false, Noise: NoiseSuppression.Off));
        var o = Run(processor, new float[tone.Length], tone);
        Assert.InRange(Db(Rms(o, Rate, 2 * Rate) / Rms(tone, Rate, 2 * Rate)), -1, 1);
    }

    [Fact]
    public void Settings_can_be_changed_while_running()
    {
        var voice = Speech(12, 120, 2, 0.3);
        var mic = Mix(voice, Hiss(12, 0.02, 5));
        using var processor = Make(new VoiceProcessing(EchoCancellation: false, Noise: NoiseSuppression.Off));
        var off = Run(processor, new float[mic.Length], mic);
        processor.Reconfigure(new VoiceProcessing(EchoCancellation: false, Noise: NoiseSuppression.High));
        var on = Run(processor, new float[mic.Length], mic);
        Assert.True(Rms(on, 8 * Rate, 12 * Rate) < Rms(off, 8 * Rate, 12 * Rate), "turning noise suppression on changed nothing");
    }

    [Fact]
    public void Odd_sized_playback_blocks_and_a_wrong_sized_frame_are_handled()
    {
        using var processor = Make(new VoiceProcessing());
        foreach (var size in new[] { 1, 7, 441, 480, 1000, 960, 3 })
            processor.ProcessPlayback(Hiss(1, 0.1, size).AsSpan(0, size));
        Assert.Throws<ArgumentException>(() => processor.ProcessCapture(new float[100]));
        var frame = Hiss(1, 0.05, 3).AsSpan(0, Frame).ToArray();
        processor.ProcessCapture(frame);
        Assert.All(frame, s => Assert.True(float.IsFinite(s)));
    }

    [Fact]
    public void Using_it_after_it_is_disposed_does_nothing_instead_of_crashing()
    {
        var processor = Make(new VoiceProcessing());
        processor.Dispose();
        processor.Dispose();
        var frame = Hiss(1, 0.05, 3).AsSpan(0, Frame).ToArray();
        var before = frame.ToArray();
        processor.ProcessPlayback(frame);
        processor.ProcessCapture(frame);
        Assert.Equal(before, frame);
    }
}
