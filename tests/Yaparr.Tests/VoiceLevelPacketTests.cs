using Concentus;
using Concentus.Enums;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;
using Yaparr.Voice;

namespace Yaparr.Tests;

// The level and speech flag in the low byte of a voice packet's timestamp (see VoicePacket), and what receivers do with it.
public class VoiceLevelPacketTests
{
    [Theory]
    [InlineData(0f, true)]
    [InlineData(-3f, true)]
    [InlineData(-20f, false)]
    [InlineData(-54f, true)]
    [InlineData(-126f, false)]
    public void A_level_survives_the_packet_to_within_a_step(float dbfs, bool speech)
    {
        var packet = new byte[VoicePacket.HeaderSize + 3];
        int length = VoicePacket.Write(packet, 0xABCD1234, 77, 960u * 1000, true, new byte[] { 1, 2, 3 }, VoicePacket.EncodeLevel(dbfs, speech));
        Assert.True(VoicePacket.TryParse(packet.AsSpan(0, length), out var tag, out var seq, out var marker, out var timestamp, out var opus));
        Assert.Equal(0xABCD1234u, tag);
        Assert.Equal((ushort)77, seq);
        Assert.True(marker);
        Assert.Equal(new byte[] { 1, 2, 3 }, opus.ToArray());
        Assert.True(VoicePacket.TryDecodeLevel(timestamp, out var read, out var readSpeech));
        Assert.InRange(read, dbfs - 1.01f, dbfs + 1.01f);
        Assert.Equal(speech, readSpeech);
        // The rest of the timestamp is still counting.
        Assert.Equal(960u * 1000 & ~0xFFu, timestamp & ~0xFFu);
    }

    [Fact]
    public void Levels_outside_the_range_are_clamped_and_nan_is_silence()
    {
        VoicePacket.TryDecodeLevel(VoicePacket.EncodeLevel(+12f, true), out var loud, out _);
        Assert.Equal(0f, loud);
        VoicePacket.TryDecodeLevel(VoicePacket.EncodeLevel(-300f, false), out var quiet, out _);
        Assert.Equal(-126f, quiet);
        VoicePacket.TryDecodeLevel(VoicePacket.EncodeLevel(float.NaN, false), out var nan, out _);
        Assert.Equal(-126f, nan);
    }

    [Fact]
    public void Packets_from_older_versions_have_no_level()
    {
        // Older senders counted the timestamp in steps of 960 from 0: whatever the count, the low byte never looks like a level.
        for (uint f = 0; f < 200_000; f += 7)
            Assert.False(VoicePacket.TryDecodeLevel(f * 960u, out _, out _), $"timestamp {f * 960u}");
        // The count wraps around 2^32 and stays a multiple of 64 (an old sender talking for days).
        Assert.False(VoicePacket.TryDecodeLevel(unchecked((uint)(5_000_000UL * 960)), out _, out _));
        Assert.False(VoicePacket.TryDecodeLevel(0, out _, out _));
    }

    [Fact]
    public void A_packet_without_a_level_is_written_as_it_always_was()
    {
        var packet = new byte[VoicePacket.HeaderSize + 1];
        VoicePacket.Write(packet, 1, 2, 960u * 5, false, new byte[] { 9 });
        Assert.Equal(new byte[] { 1, 0, 0, 0, 1, 0, 2, 0, 0, 0x12, 0xC0, 9 }, packet);   // 960 * 5 = 4800 = 0x12C0
    }

    [Fact]
    public void Speech_is_told_from_noise_and_silence()
    {
        var detector = new SpeechDetector();
        var rng = new Random(3);
        float[] Frame(double amplitude, bool voiced)
        {
            var f = new float[960];
            for (int i = 0; i < f.Length; i++)
                f[i] = (float)(voiced ? amplitude * Math.Sin(2 * Math.PI * 220 * i / 48000) : amplitude * (rng.NextDouble() * 2 - 1));
            return f;
        }
        for (int i = 0; i < 50; i++) detector.Update(Frame(0.001, false), out _);   // a quiet room
        detector.Update(Frame(0.001, false), out bool noise);
        Assert.False(noise);
        float db = detector.Update(Frame(0.1, true), out bool talking);
        Assert.True(talking);
        Assert.InRange(db, -25f, -20f);   // a 0.1 sine is -23 dBFS RMS
        detector.Update(new float[960], out bool silent);
        Assert.False(silent);
    }

    private static byte[] NoisePacket(IOpusEncoder encoder, Random random, double amplitude)
    {
        var pcm = new float[960];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)((random.NextDouble() * 2 - 1) * amplitude);
        var packet = new byte[1275];
        int length = encoder.Encode(pcm, 960, packet, 1275);
        return packet.AsSpan(0, length).ToArray();
    }

    [Fact]
    public async Task In_a_big_call_the_people_who_say_they_are_talking_are_decoded_even_if_a_noisy_microphone_sends_bigger_packets()
    {
        var hub = new InMemoryDiscoveryHub();
        var a = TestUtil.NewNode(hub, "alice");
        var b = TestUtil.NewNode(hub, "bob");
        var ra = await a.JoinRoomAsync("Lobby");
        var rb = await b.JoinRoomAsync("Lobby");
        await TestUtil.WaitFor(() => ra.Members.Any(m => m.Id == b.Id) && rb.Members.Any(m => m.Id == a.Id));
        using var _ = a; using var __ = b;
        using var listener = rb.JoinVoice();

        // Twelve senders, all with loud, noisy packets of the same size; only four say they are talking.
        var random = new Random(7);
        var senders = Enumerable.Range(0, 12).Select(i => (Id: PeerIdentity.Generate().Id, Talking: i < 4,
            Encoder: OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP))).ToList();
        foreach (var t in senders) { t.Encoder.Bitrate = 32000; t.Encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE; }

        const int frames = 150;
        var speaker = new float[960];
        for (int f = 0; f < frames; f++)
        {
            var buffer = new byte[VoicePacket.HeaderSize + 1275];
            foreach (var t in senders)
            {
                var opus = NoisePacket(t.Encoder, random, 0.5);
                byte level = VoicePacket.EncodeLevel(t.Talking ? -20f : -60f, t.Talking);
                int length = VoicePacket.Write(buffer, listener.Tag, (ushort)f, (uint)(f * 960), false, opus, level);
                listener.OnPacket(t.Id, buffer.AsSpan(0, length));
            }
            listener.PullPlayback(speaker);
        }

        foreach (var t in senders.Where(t => t.Talking))
            Assert.True(listener.DecodedFrames(t.Id) >= frames * 0.8, $"someone who said they were talking was decoded only {listener.DecodedFrames(t.Id)} of {frames} frames");
        Assert.True(senders.Where(t => !t.Talking).Sum(t => listener.DecodedFrames(t.Id)) < 8 * frames * 0.6);
        Assert.Equal(true, listener.SpeakerSignal(senders[0].Id)?.Speech);
        Assert.Equal(false, listener.SpeakerSignal(senders[11].Id)?.Speech);
    }

    [Fact]
    public async Task A_sender_that_says_nothing_is_ranked_by_packet_size_as_before_and_has_no_signal()
    {
        var hub = new InMemoryDiscoveryHub();
        var a = TestUtil.NewNode(hub, "alice");
        var ra = await a.JoinRoomAsync("Lobby");
        using var _ = a;
        using var listener = ra.JoinVoice();
        var old = PeerIdentity.Generate().Id;
        var encoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        var buffer = new byte[VoicePacket.HeaderSize + 1275];
        int length = VoicePacket.Write(buffer, listener.Tag, 0, 960u * 3, false, NoisePacket(encoder, new Random(1), 0.3));
        listener.OnPacket(old, buffer.AsSpan(0, length));
        Assert.Null(listener.SpeakerSignal(old));
    }

    [Collection("real-time audio")]
    public class InSessions
    {
        [Fact]
        public async Task A_listener_is_told_how_loud_the_other_side_is_and_whether_they_are_talking()
        {
            var hub = new InMemoryDiscoveryHub();
            var a = TestUtil.NewNode(hub, "alice");
            var b = TestUtil.NewNode(hub, "bob");
            var ra = await a.JoinRoomAsync("Lobby");
            var rb = await b.JoinRoomAsync("Lobby");
            await TestUtil.WaitFor(() => ra.Members.Any(m => m.Id == b.Id) && rb.Members.Any(m => m.Id == a.Id));
            using var _ = a; using var __ = b;
            using var va = ra.JoinVoice();
            using var vb = rb.JoinVoice();
            va.Activation = vb.Activation = VoiceActivation.Open;
            await TestUtil.WaitFor(() => ra.VoiceParticipants.Count == 2 && rb.VoiceParticipants.Count == 2);

            var buffer = new float[480];
            int seen = 0, talkingSeen = 0;
            double dbSum = 0;
            long next = Environment.TickCount64;
            // Four seconds of a tone in bursts at amplitude 0.1 (about -23 dBFS while it sounds, quiet between).
            for (int i = 0; i < 400; i++)
            {
                va.PushCapture(Enumerable.Range(0, 480).Select(k =>
                {
                    int n = i * 480 + k;
                    double t = n / 48000.0;
                    double env = Math.Sin(2 * Math.PI * 1.2 * t) > 0 ? 1 : 0;
                    return (float)(0.1 * env * Math.Sin(2 * Math.PI * 220 * t));
                }).ToArray());
                vb.PullPlayback(buffer);
                if (i > 100 && vb.SpeakerSignal(a.Id) is { } s)
                {
                    seen++;
                    if (s.Speech) { talkingSeen++; dbSum += s.Dbfs; }
                }
                next += 10;
                long wait = next - Environment.TickCount64;
                if (wait > 0) await Task.Delay((int)wait);
            }
            Assert.True(seen > 200, $"the signal was there only {seen} times");
            Assert.True(talkingSeen > 50, $"talking was reported only {talkingSeen} times");
            Assert.InRange(dbSum / talkingSeen, -30, -16);
        }
    }
}
