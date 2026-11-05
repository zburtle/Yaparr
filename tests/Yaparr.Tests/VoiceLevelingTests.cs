using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Voice;

namespace Yaparr.Tests;

// Automatic volume (the microphone) and the speaker leveller (each person heard), through real sessions in real time.
[Collection("real-time audio")]
public class VoiceLevelingTests
{
    // Talking in bursts: a tone with a rising and falling loudness and pauses between, at a given peak amplitude.
    private static float Burst(int index, double amplitude)
    {
        double t = index / 48000.0;
        double env = Math.Pow(Math.Max(0, Math.Sin(2 * Math.PI * 1.2 * t + 0.3)), 0.6);
        return (float)(amplitude * env * Math.Sin(2 * Math.PI * 220 * t));
    }

    private static async Task<(ChatNode A, ChatNode B, VoiceSession Va, VoiceSession Vb)> Pair()
    {
        var hub = new InMemoryDiscoveryHub();
        var a = TestUtil.NewNode(hub, "alice");
        var b = TestUtil.NewNode(hub, "bob");
        var ra = await a.JoinRoomAsync("Lobby");
        var rb = await b.JoinRoomAsync("Lobby");
        await TestUtil.WaitFor(() => ra.Members.Any(m => m.Id == b.Id) && rb.Members.Any(m => m.Id == a.Id));
        var va = ra.JoinVoice();
        var vb = rb.JoinVoice();
        va.Activation = vb.Activation = VoiceActivation.Open;
        await TestUtil.WaitFor(() => ra.VoiceParticipants.Count == 2 && rb.VoiceParticipants.Count == 2);
        return (a, b, va, vb);
    }

    // Plays the part of a sound card for `seconds`; returns the loudness (RMS, dB) of what is heard in the last `lastSeconds`, over the louder frames.
    private static async Task<double> Talk(VoiceSession speaker, VoiceSession listener, double amplitude, int seconds, int lastSeconds)
    {
        var buffer = new float[480];
        var heard = new List<float>();
        int offset = 0;
        long next = Environment.TickCount64;
        for (int i = 0; i < seconds * 100; i++)
        {
            speaker.PushCapture(Enumerable.Range(0, 480).Select(k => Burst(offset + k, amplitude)).ToArray());
            offset += 480;
            listener.PullPlayback(buffer);
            heard.AddRange(buffer);
            next += 10;
            long wait = next - Environment.TickCount64;
            if (wait > 0) await Task.Delay((int)wait);
        }
        double sum = 0; int n = 0;
        int from = (seconds - lastSeconds) * 48000;
        for (int at = from; at + 480 <= heard.Count; at += 480)
        {
            double e = 0;
            for (int k = 0; k < 480; k++) e += heard[at + k] * (double)heard[at + k];
            e /= 480;
            if (10 * Math.Log10(e + 1e-12) > -50) { sum += e; n++; }
        }
        return 10 * Math.Log10(sum / Math.Max(1, n) + 1e-12);
    }

    [Fact]
    public async Task A_quiet_voice_is_raised_and_a_loud_one_lowered_for_the_listener()
    {
        var (a, b, va, vb) = await Pair();
        using var _ = a; using var __ = b; using var ___ = va; using var ____ = vb;

        Assert.False(vb.LevelSpeakers);
        double quietOff = await Talk(va, vb, 0.04, 4, 2);
        Assert.Equal(0f, vb.SpeakerLevelGainDb(a.Id));

        vb.LevelSpeakers = true;
        double quietOn = await Talk(va, vb, 0.04, 8, 2);
        Assert.True(vb.SpeakerLevelGainDb(a.Id) > 4, $"gain {vb.SpeakerLevelGainDb(a.Id):0.0} dB");
        Assert.True(quietOn > quietOff + 4, $"quiet voice: {quietOff:0.0} dB without, {quietOn:0.0} dB with");

        // A loud voice in the same call is brought down.
        double loudOn = await Talk(va, vb, 0.9, 8, 2);
        Assert.True(vb.SpeakerLevelGainDb(a.Id) < -3, $"gain {vb.SpeakerLevelGainDb(a.Id):0.0} dB");
        Assert.True(loudOn < quietOn + 8, $"after leveling the loud voice is {loudOn - quietOn:0.0} dB above the quiet one");

        // Turned off again: back to no change.
        vb.LevelSpeakers = false;
        Assert.Equal(0f, vb.SpeakerLevelGainDb(a.Id));
    }

    [Fact]
    public async Task Automatic_volume_raises_a_quiet_microphone_before_it_is_sent()
    {
        var (a, b, va, vb) = await Pair();
        using var _ = a; using var __ = b; using var ___ = va; using var ____ = vb;

        Assert.False(va.AutomaticVolume);
        double off = await Talk(va, vb, 0.04, 4, 2);
        va.AutomaticVolume = true;
        double on = await Talk(va, vb, 0.04, 8, 2);
        Assert.True(on > off + 4, $"quiet microphone: {off:0.0} dB without, {on:0.0} dB with");
        va.AutomaticVolume = false;
        double offAgain = await Talk(va, vb, 0.04, 4, 2);
        Assert.True(Math.Abs(offAgain - off) < 2, $"back to {offAgain:0.0} dB, was {off:0.0} dB");
    }
}
