using Concentus;
using Concentus.Enums;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;
using Yaparr.Voice;

namespace Yaparr.Tests;

[Collection("real-time audio")]
public class VoiceTests
{
    private static float[] Tone(double hz, int samples, int offset = 0, double amplitude = 0.4) =>
        Enumerable.Range(0, samples).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * (i + offset) / 48000))).ToArray();

    // Share of the signal's energy at one frequency (1.0 would be a pure tone at that frequency).
    private static double ShareAt(IReadOnlyList<float> samples, double hz)
    {
        double re = 0, im = 0, total = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            double a = 2 * Math.PI * hz * i / 48000;
            re += samples[i] * Math.Cos(a); im += samples[i] * Math.Sin(a); total += samples[i] * samples[i];
        }
        return total <= 0 ? 0 : 2 * (re * re + im * im) / (samples.Count * total);
    }

    private static List<(ushort Seq, byte[] Data)> EncodeTone(int frames, double hz)
    {
        var encoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = 32000; encoder.UseInbandFEC = true; encoder.PacketLossPercent = 15;
        var packets = new List<(ushort, byte[])>();
        var buffer = new byte[400];
        for (int f = 0; f < frames; f++)
        {
            int len = encoder.Encode(Tone(hz, 960, f * 960), 960, buffer, buffer.Length);
            packets.Add(((ushort)(65530 + f), buffer.AsSpan(0, len).ToArray())); // starts just before the sequence wraps
        }
        return packets;
    }

    // Plays the stream the way a sound card would: one 20 ms frame per tick, with each packet arriving at its tick.
    private static List<float> Simulate(RemoteVoice remote, IEnumerable<(int Tick, ushort Seq, byte[] Data)> arrivals, int ticks)
    {
        var pending = arrivals.OrderBy(a => a.Tick).ToList();
        var heard = new List<float>();
        var frame = new float[960];
        int next = 0;
        for (int t = 0; t < ticks; t++)
        {
            while (next < pending.Count && pending[next].Tick <= t) { remote.Enqueue(pending[next].Seq, pending[next].Data); next++; }
            if (remote.ReadFrame(frame, t * 20)) heard.AddRange(frame);
        }
        return heard;
    }

    [Fact]
    public void A_clean_stream_comes_out_as_the_same_tone_across_a_sequence_wrap()
    {
        using var remote = new RemoteVoice(PeerIdentity.Generate().Id);
        var packets = EncodeTone(50, 440);
        var heard = Simulate(remote, packets.Select((p, i) => (i, p.Seq, p.Data)), 60);
        Assert.True(heard.Count >= 40 * 960, $"only {heard.Count / 960} frames came out");
        Assert.True(ShareAt(heard.Skip(4000).ToList(), 440) > 0.9);
        Assert.Equal(0, remote.Lost);
    }

    [Fact]
    public void Reordered_packets_are_put_back_in_order()
    {
        using var remote = new RemoteVoice(PeerIdentity.Generate().Id);
        var packets = EncodeTone(40, 300);
        // Each even packet arrives two ticks late, so it comes after the odd packet that follows it.
        var arrivals = packets.Select((p, i) => (Tick: i % 2 == 0 ? i + 2 : i, p.Seq, p.Data));
        var heard = Simulate(remote, arrivals, 50);
        Assert.True(ShareAt(heard.Skip(4000).ToList(), 300) > 0.9);
        Assert.Equal(0, remote.Lost);
    }

    [Fact]
    public void Lost_packets_are_covered_and_the_tone_survives()
    {
        using var remote = new RemoteVoice(PeerIdentity.Generate().Id);
        var rng = new Random(7);
        int dropped = 0;
        var arrivals = new List<(int, ushort, byte[])>();
        foreach (var (p, i) in EncodeTone(100, 500).Select((p, i) => (p, i)))
        {
            if (i > 0 && rng.NextDouble() < 0.2) { dropped++; continue; }
            arrivals.Add((i, p.Seq, p.Data));
        }
        var heard = Simulate(remote, arrivals, 110);
        Assert.True(dropped > 10);
        Assert.True(remote.Lost >= dropped - 3, $"lost={remote.Lost} dropped={dropped}");
        Assert.True(heard.Count >= 80 * 960);
        Assert.True(ShareAt(heard.Skip(4000).ToList(), 500) > 0.6);
    }

    [Fact]
    public void Silence_stops_playback_so_the_next_burst_starts_clean()
    {
        using var remote = new RemoteVoice(PeerIdentity.Generate().Id);
        var changes = new List<bool>();
        remote.SpeakingChanged += changes.Add;
        Simulate(remote, EncodeTone(10, 440).Select((p, i) => (i, p.Seq, p.Data)), 30);
        Assert.False(remote.Speaking);
        Assert.Equal(new[] { true, false }, changes);
    }

    private static async Task<(ChatNode A, ChatNode B, ChatRoom Ra, ChatRoom Rb)> Pair(InMemoryDiscoveryHub hub)
    {
        var a = TestUtil.NewNode(hub, "alice");
        var b = TestUtil.NewNode(hub, "bob");
        var ra = await a.JoinRoomAsync("Lobby");
        var rb = await b.JoinRoomAsync("Lobby");
        await TestUtil.WaitFor(() => ra.Members.Any(m => m.Id == b.Id) && rb.Members.Any(m => m.Id == a.Id));
        return (a, b, ra, rb);
    }

    // The longest run of consecutive 10 ms frames that are not silent.
    private static List<float> LongestAudibleRun(List<float> samples)
    {
        List<float> best = new(), current = new();
        for (int i = 0; i + 480 <= samples.Count; i += 480)
        {
            var frame = samples.GetRange(i, 480);
            if (frame.Max(Math.Abs) > 0.01f) current.AddRange(frame);
            else { if (current.Count > best.Count) best = current; current = new(); }
        }
        return current.Count > best.Count ? current : best;
    }

    // Plays the part of a sound card: feeds the microphone in real time and collects what the speaker would play.
    private static async Task<List<float>> Talk(VoiceSession speaker, VoiceSession listener, double hz, int seconds)
    {
        var heard = new List<float>();
        var buffer = new float[480];
        int offset = 0;
        long next = Environment.TickCount64;
        for (int i = 0; i < seconds * 100; i++)
        {
            speaker.PushCapture(Tone(hz, 480, offset)); offset += 480;
            listener.PullPlayback(buffer);
            heard.AddRange(buffer);
            next += 10;
            long wait = next - Environment.TickCount64;
            if (wait > 0) await Task.Delay((int)wait);
        }
        return heard;
    }

    [Fact]
    public async Task Two_people_join_a_voice_channel_and_hear_each_other()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, ra, rb) = await Pair(hub);
        using var _ = a; using var __ = b;

        using var va = ra.JoinVoice();
        va.Activation = VoiceActivation.Open;
        using var vb = rb.JoinVoice();
        vb.Activation = VoiceActivation.Open;

        await TestUtil.WaitFor(() => ra.VoiceParticipants.Count == 2 && rb.VoiceParticipants.Count == 2);
        Assert.Contains(rb.VoiceParticipants, p => p.Peer.Id == a.Id && !p.IsSelf);
        Assert.Contains(ra.VoiceParticipants, p => p.IsSelf && p.Peer.Id == a.Id);

        var heard = await Talk(va, vb, 440, 3);
        // A busy machine delays packets and leaves gaps in what the speaker would play; judge the longest stretch of sound.
        var run = LongestAudibleRun(heard);
        Assert.True(run.Count >= 30 * 480, $"longest stretch of sound was only {run.Count / 480} frames");
        Assert.True(ShareAt(run, 440) > 0.5, $"share at 440 Hz was {ShareAt(run, 440):0.00}");
        Assert.True(run.Max(Math.Abs) > 0.1f);
    }

    [Fact]
    public async Task A_muted_person_is_shown_as_muted_and_nothing_is_sent()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, ra, rb) = await Pair(hub);
        using var _ = a; using var __ = b;

        using var va = ra.JoinVoice();
        va.Activation = VoiceActivation.Open;
        using var vb = rb.JoinVoice();
        await TestUtil.WaitFor(() => rb.VoiceParticipants.Count == 2);

        va.Muted = true;
        await TestUtil.WaitFor(() => rb.VoiceParticipants.Any(p => p.Peer.Id == a.Id && p.Muted));

        var heard = await Talk(va, vb, 440, 1);
        Assert.True(heard.Max(Math.Abs) < 0.01f);
        Assert.Equal(0, vb.Stats(a.Id).Received);
    }

    [Fact]
    public async Task Leaving_removes_you_from_the_channel_for_everyone()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, ra, rb) = await Pair(hub);
        using var _ = a; using var __ = b;

        var va = ra.JoinVoice();
        using var vb = rb.JoinVoice();
        await TestUtil.WaitFor(() => rb.VoiceParticipants.Count == 2);

        va.Leave();
        await TestUtil.WaitFor(() => rb.VoiceParticipants.Count == 1);
        Assert.Null(ra.CurrentVoice);
        // Alice is no longer in the channel, but she can still see who is.
        Assert.Equal(new[] { b.Id }, ra.VoiceParticipants.Select(p => p.Peer.Id));
    }

    private sealed class RecordingProcessor : IVoiceProcessor
    {
        public int CaptureFrames, WrongSizes;
        public long Played;
        public Action<Span<float>>? OnCapture;
        public void ProcessCapture(Span<float> frame)
        {
            Interlocked.Increment(ref CaptureFrames);
            if (frame.Length != VoiceSession.FrameSamples) Interlocked.Increment(ref WrongSizes);
            OnCapture?.Invoke(frame);
        }
        public void ProcessPlayback(ReadOnlySpan<float> played) => Interlocked.Add(ref Played, played.Length);
        public void Dispose() { }
    }

    [Fact]
    public async Task A_processor_is_given_every_microphone_frame_and_everything_sent_to_the_speakers()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, ra, rb) = await Pair(hub);
        using var _ = a; using var __ = b;
        using var va = ra.JoinVoice();
        using var vb = rb.JoinVoice();
        va.Activation = vb.Activation = VoiceActivation.Open;
        var mic = new RecordingProcessor();
        var speakers = new RecordingProcessor();
        va.Processor = mic;
        vb.Processor = speakers;
        await TestUtil.WaitFor(() => rb.VoiceParticipants.Count == 2);

        var heard = await Talk(va, vb, 440, 2); // 200 pushes of 480 samples = 100 frames of 20 ms
        Assert.Equal(100, mic.CaptureFrames);
        Assert.Equal(0, mic.WrongSizes);
        Assert.Equal(96000, speakers.Played);
        Assert.True(ShareAt(LongestAudibleRun(heard), 440) > 0.5); // a processor that changes nothing changes nothing
    }

    [Fact]
    public async Task What_a_processor_does_to_the_microphone_is_what_gets_sent()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, ra, rb) = await Pair(hub);
        using var _ = a; using var __ = b;
        using var va = ra.JoinVoice();
        using var vb = rb.JoinVoice();
        va.Activation = vb.Activation = VoiceActivation.Open;
        va.Processor = new RecordingProcessor { OnCapture = frame => frame.Clear() };
        await TestUtil.WaitFor(() => rb.VoiceParticipants.Count == 2);

        var heard = await Talk(va, vb, 440, 1);
        Assert.True(vb.Stats(a.Id).Received > 20);
        Assert.True(heard.Max(Math.Abs) < 0.01f);
    }

    [Fact]
    public async Task A_processor_that_fails_is_switched_off_and_the_microphone_carries_on()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, ra, rb) = await Pair(hub);
        using var _ = a; using var __ = b;
        using var va = ra.JoinVoice();
        using var vb = rb.JoinVoice();
        va.Activation = vb.Activation = VoiceActivation.Open;
        va.Processor = new RecordingProcessor { OnCapture = _ => throw new InvalidOperationException("broken") };
        int failures = 0;
        va.ProcessorFailed += _ => Interlocked.Increment(ref failures);
        await TestUtil.WaitFor(() => rb.VoiceParticipants.Count == 2);

        var heard = await Talk(va, vb, 440, 2);
        Assert.Null(va.Processor);
        Assert.Equal(1, failures);
        Assert.True(ShareAt(LongestAudibleRun(heard), 440) > 0.5);
    }

    [Fact]
    public async Task Voice_activation_sends_only_while_talking()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, ra, rb) = await Pair(hub);
        using var _ = a; using var __ = b;

        using var va = ra.JoinVoice();
        using var vb = rb.JoinVoice();
        await TestUtil.WaitFor(() => rb.VoiceParticipants.Count == 2);

        var quiet = new float[480];
        for (int i = 0; i < 100; i++) { va.PushCapture(quiet); await Task.Delay(10); }
        Assert.False(va.IsSending);
        Assert.Equal(0, vb.Stats(a.Id).Received);

        await Talk(va, vb, 440, 2);
        Assert.True(vb.Stats(a.Id).Received > 20);
    }
}
