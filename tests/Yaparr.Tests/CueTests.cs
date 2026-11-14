using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Voice;

namespace Yaparr.Tests;

// The app's own sounds are played through the call's speakers, so the echo canceller is told of them.
[Collection("real-time audio")]
public class CueTests
{
    private sealed class Recorder : IVoiceProcessor
    {
        public readonly List<float> Played = new();
        public void ProcessCapture(Span<float> frame) { }
        public void ProcessPlayback(ReadOnlySpan<float> played) { lock (Played) Played.AddRange(played.ToArray()); }
        public void Dispose() { }
    }

    private static float[] Tone(double seconds, double amplitude = 0.3) =>
        Enumerable.Range(0, (int)(48000 * seconds)).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * 1000 * i / 48000))).ToArray();

    private static float[] Listen(VoiceSession session, int frames)
    {
        var all = new float[frames * 960];
        for (int f = 0; f < frames; f++) session.PullPlayback(all.AsSpan(f * 960, 960));
        return all;
    }

    private static double Rms(float[] s) => Math.Sqrt(s.Sum(x => (double)x * x) / Math.Max(1, s.Length));

    [Fact]
    public async Task A_cue_comes_out_of_the_speakers_and_is_given_to_the_echo_canceller_even_when_deafened()
    {
        var hub = new InMemoryDiscoveryHub();
        using var node = TestUtil.NewNode(hub, "cue");
        var room = await node.JoinRoomAsync("cues");
        using var session = room.JoinVoice();
        var recorder = new Recorder();
        session.Processor = recorder;
        session.Deafened = true;
        session.SoundboardsMuted = true;

        Listen(session, 2);                                   // the speakers are being fed
        Assert.True(session.PlayCue(Tone(0.2)));
        var heard = Listen(session, 20);

        Assert.True(Rms(heard) > 0.05, "the cue should be heard");
        double reference;
        lock (recorder.Played) reference = Rms(recorder.Played.ToArray());
        Assert.True(reference > 0.05, "the echo canceller should have been given the cue");
        Assert.True(Rms(Listen(session, 5)) < 0.001, "and it should end");
    }

    [Fact]
    public async Task A_cue_is_refused_when_the_speakers_are_not_being_fed_or_the_call_has_ended()
    {
        var hub = new InMemoryDiscoveryHub();
        using var node = TestUtil.NewNode(hub, "cue2");
        var room = await node.JoinRoomAsync("cues2");
        var session = room.JoinVoice();
        Assert.False(session.PlayCue(Tone(0.1)));             // nothing has pulled playback yet
        Listen(session, 1);
        Assert.True(session.PlayCue(Tone(0.1)));
        Assert.False(session.PlayCue(Tone(5)));               // too long to be a cue
        session.Dispose();
        Assert.False(session.PlayCue(Tone(0.1)));
    }
}
