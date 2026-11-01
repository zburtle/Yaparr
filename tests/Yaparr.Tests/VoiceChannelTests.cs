using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Voice;

namespace Yaparr.Tests;

[Collection("real-time audio")]
public class VoiceChannelTests
{
    private static float[] Tone(double hz, int samples, int offset) =>
        Enumerable.Range(0, samples).Select(i => (float)(0.4 * Math.Sin(2 * Math.PI * hz * (i + offset) / 48000))).ToArray();

    // The part of a sound card: feeds the speaker's microphone in real time and collects what each listener would hear.
    private static async Task<List<float>[]> Talk(VoiceSession speaker, int seconds, params VoiceSession[] listeners)
    {
        var heard = listeners.Select(_ => new List<float>()).ToArray();
        var buffer = new float[480];
        int offset = 0;
        long next = Environment.TickCount64;
        for (int i = 0; i < seconds * 100; i++)
        {
            speaker.PushCapture(Tone(440, 480, offset)); offset += 480;
            for (int l = 0; l < listeners.Length; l++) { listeners[l].PullPlayback(buffer); heard[l].AddRange(buffer); }
            next += 10;
            long wait = next - Environment.TickCount64;
            if (wait > 0) await Task.Delay((int)wait);
        }
        return heard;
    }

    private static float Loudest(List<float> samples) => samples.Count == 0 ? 0 : samples.Max(Math.Abs);

    private sealed record Trio(ChatNode A, ChatNode B, ChatNode C, ChatRoom Ra, ChatRoom Rb, ChatRoom Rc, string Lounge);

    private static async Task<Trio> StartTrio()
    {
        var hub = new InMemoryDiscoveryHub();
        var a = TestUtil.NewNode(hub, "alice");
        var b = TestUtil.NewNode(hub, "bob");
        var c = TestUtil.NewNode(hub, "carol");
        var key = RoomKey.Generate(a.AccountId);
        var ra = await a.JoinRoomAsync(key, "lobby");
        var rb = await b.JoinRoomAsync(RoomKey.Parse(key.ToString()), "lobby");
        var rc = await c.JoinRoomAsync(RoomKey.Parse(key.ToString()), "lobby");
        await TestUtil.WaitFor(() => ra.Members.Count >= 2 && rb.Members.Count >= 2 && rc.Members.Count >= 2, 30_000);
        var lounge = ra.CreateChannel("Lounge", ChannelKind.Voice);
        await TestUtil.WaitFor(() => rb.Channels.Any(x => x.Id == lounge.Id) && rc.Channels.Any(x => x.Id == lounge.Id), 20_000);
        return new Trio(a, b, c, ra, rb, rc, lounge.Id);
    }

    [Fact]
    public async Task People_in_different_voice_channels_do_not_hear_each_other_and_moving_puts_you_with_the_others()
    {
        var t = await StartTrio();
        using var _ = t.A; using var __ = t.B; using var ___ = t.C;

        using var va = t.Ra.JoinVoice();
        va.Activation = VoiceActivation.Open;
        using var vb = t.Rb.JoinVoice();
        using var vc = t.Rc.JoinVoice(t.Lounge);
        Assert.Equal(RoomChannels.DefaultVoice, va.ChannelId);
        Assert.Equal(t.Lounge, vc.ChannelId);

        // Everyone in the room sees who is in which channel.
        await TestUtil.WaitFor(() => t.Rb.VoiceParticipants.Count == 3 && t.Rc.VoiceParticipants.Count == 3, 20_000);
        Assert.Equal(t.Lounge, t.Ra.VoiceParticipants.Single(p => p.Peer.Id == t.C.Id).Channel);
        Assert.Equal(RoomChannels.DefaultVoice, t.Rc.VoiceParticipants.Single(p => p.Peer.Id == t.B.Id).Channel);

        var heard = await Talk(va, 3, vb, vc);
        Assert.True(Loudest(heard[0]) > 0.1f, "bob is in alice's channel and should hear her");
        Assert.True(Loudest(heard[1]) < 0.01f, "carol is in another channel and should hear nothing");

        // Carol moves to the first channel and now hears her.
        using var vc2 = t.Rc.JoinVoice();
        await TestUtil.WaitFor(() => t.Ra.VoiceParticipants.Single(p => p.Peer.Id == t.C.Id).Channel == RoomChannels.DefaultVoice, 20_000);
        var again = await Talk(va, 3, vc2);
        Assert.True(Loudest(again[0]) > 0.1f, "carol moved into alice's channel and should hear her");
    }

    [Fact]
    public async Task Only_a_voice_channel_that_exists_can_be_joined()
    {
        var t = await StartTrio();
        using var _ = t.A; using var __ = t.B; using var ___ = t.C;
        var games = t.Ra.CreateChannel("games", ChannelKind.Text);
        await TestUtil.WaitFor(() => t.Rb.Channels.Any(x => x.Id == games.Id), 20_000);
        Assert.Throws<InvalidOperationException>(() => t.Rb.JoinVoice(games.Id));
        Assert.Throws<InvalidOperationException>(() => t.Rb.JoinVoice("1234567890abcdef"));

        t.Ra.DeleteChannel(t.Lounge);
        await TestUtil.WaitFor(() => t.Rb.Channels.All(x => x.Id != t.Lounge), 20_000);
        Assert.Throws<InvalidOperationException>(() => t.Rb.JoinVoice(t.Lounge));
    }

    [Fact]
    public async Task A_shared_screen_belongs_to_the_voice_channel_of_the_sharer()
    {
        var t = await StartTrio();
        using var _ = t.A; using var __ = t.B; using var ___ = t.C;
        using var share = t.A.StartMedia(t.Ra, "screen", "alice's screen", channel: t.Lounge);
        using var plain = t.A.StartMedia(t.Ra, "screen", "alice's window");
        await TestUtil.WaitFor(() => t.Rb.MediaGroups.Count == 2, 20_000);
        Assert.Equal(t.Lounge, t.Rb.MediaGroups.Single(g => g.Name == "alice's screen").Channel);
        Assert.Null(t.Rb.MediaGroups.Single(g => g.Name == "alice's window").Channel);
    }

    [Fact]
    public void A_voice_state_is_signed_with_its_channel()
    {
        var who = Yaparr.Identity.PeerIdentity.Generate();
        var first = SignedVoice.Create(who, "room", "alice", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), true, false, false, RoomChannels.DefaultVoice);
        Assert.Null(first.Chan);
        Assert.True(SignedVoice.Verify(first, "room"));
        var other = SignedVoice.Create(who, "room", "alice", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), true, false, false, "1234567890abcdef");
        Assert.True(SignedVoice.Verify(other, "room"));
        Assert.False(SignedVoice.Verify(other with { Chan = "fedcba0987654321" }, "room"));
        Assert.False(SignedVoice.Verify(other with { Chan = null }, "room"));
    }

    [Fact]
    public async Task Someone_in_the_call_who_goes_silent_is_reported_out_of_reach_but_one_who_leaves_is_not()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = TestUtil.NewNode(hub, "alice");
        using var b = TestUtil.NewNode(hub, "bob");
        using var c = TestUtil.NewNode(hub, "carol");
        var ra = await a.JoinRoomAsync("reach");
        var rb = await b.JoinRoomAsync("reach");
        var rc = await c.JoinRoomAsync("reach");
        await TestUtil.WaitFor(() => ra.MemberStates.Count == 2 && rb.MemberStates.Count == 2 && rc.MemberStates.Count == 2, 30_000);
        using var va = ra.JoinVoice();
        using var vb = rb.JoinVoice();
        using var vc = rc.JoinVoice();
        await TestUtil.WaitFor(() => ra.VoiceParticipants.Count == 3, 30_000);

        var reach = ra.VoiceReach;
        Assert.Equal(2, reach.Expected);
        Assert.Empty(reach.Unreachable);

        // Carol leaves properly: she is simply not in the call any more.
        vc.Leave();
        await TestUtil.WaitFor(() => ra.VoiceReach.Expected == 1, 30_000);
        Assert.Empty(ra.VoiceReach.Unreachable);

        // People in a call ping each other every couple of seconds, which marks them as quick to judge.
        await TestUtil.WaitFor(() => a.Network.Get(b.Id)?.SendsFastPings == true, 20_000);

        // Bob's machine stops sending without a word (the connection still looks established for another half minute).
        b.Mux.OutboundFilter = (_, _) => false;
        var silentFor = System.Diagnostics.Stopwatch.StartNew();
        await TestUtil.WaitFor(() => ra.VoiceReach.Unreachable.Contains(b.Id), 40_000);
        // Six seconds of silence, and the check that sees it; it used to be fourteen.
        Assert.True(silentFor.Elapsed < TimeSpan.FromSeconds(12), $"took {silentFor.Elapsed.TotalSeconds:0.0} s");
        Assert.Equal(1, ra.VoiceReach.Expected);
        Assert.Equal(0, ra.VoiceReach.Reachable);
    }

    [Fact]
    public async Task Someone_who_is_muted_and_says_nothing_for_a_long_time_is_still_reachable()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = TestUtil.NewNode(hub, "alice");
        using var b = TestUtil.NewNode(hub, "bob");
        var ra = await a.JoinRoomAsync("quiet");
        var rb = await b.JoinRoomAsync("quiet");
        await TestUtil.WaitFor(() => ra.MemberStates.Count == 1 && rb.MemberStates.Count == 1, 30_000);
        using var va = ra.JoinVoice();
        using var vb = rb.JoinVoice();
        await TestUtil.WaitFor(() => ra.VoiceParticipants.Count == 2, 30_000);
        vb.Muted = true;   // no audio from Bob at all, nor from Alice

        // Well past the fourteen seconds after which a connection we hear nothing from is given up on; only pings and beacons are going.
        for (int second = 0; second < 25; second++)
        {
            await Task.Delay(1000);
            Assert.Empty(ra.VoiceReach.Unreachable);
        }
        Assert.Equal(1, ra.VoiceReach.Reachable);
    }
}
