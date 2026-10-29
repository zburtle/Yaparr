using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class BlockTests
{
    private static async Task<(ChatNode A, ChatNode B, ChatNode C, ChatRoom Ra, ChatRoom Rb, ChatRoom Rc)> Three(InMemoryDiscoveryHub hub)
    {
        var a = TestUtil.NewNode(hub, "alice");
        var b = TestUtil.NewNode(hub, "bob");
        var c = TestUtil.NewNode(hub, "carol");
        var ra = await a.JoinRoomAsync("lobby");
        var rb = await b.JoinRoomAsync("lobby");
        var rc = await c.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => ra.Members.Count == 2 && rb.Members.Count == 2 && rc.Members.Count == 2);
        return (a, b, c, ra, rb, rc);
    }

    [Fact]
    public async Task A_blocked_persons_messages_are_never_shown_to_you_but_others_still_get_them()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, c, ra, rb, rc) = await Three(hub);
        using var _ = a; using var __ = b; using var ___ = c;

        var changes = new List<(PeerId, bool)>();
        a.BlockChanged += (id, blocked) => changes.Add((id, blocked));
        a.Block(b.Id);
        Assert.Equal(new[] { (b.Id, true) }, changes);
        Assert.True(a.IsBlocked(b.Id));

        var seenByA = new List<string>();
        var seenByC = new TaskCompletionSource<string>();
        ra.MessageReceived += m => seenByA.Add(m.Text);
        rc.MessageReceived += m => { if (m.From.Id == b.Id) seenByC.TrySetResult(m.Text); };
        await rb.SendAsync("you cannot see this");
        Assert.Equal("you cannot see this", await seenByC.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await Task.Delay(500);
        Assert.Empty(seenByA);
        Assert.DoesNotContain(ra.GetHistory(), m => m.From.Id == b.Id);
        Assert.DoesNotContain(ra.Members, m => m.Id == b.Id);
    }

    [Fact]
    public async Task Blocking_closes_the_connection_and_it_is_not_opened_again_until_unblocked()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, c, ra, rb, rc) = await Three(hub);
        using var _ = a; using var __ = b; using var ___ = c;

        a.Block(b.Id);
        await TestUtil.WaitFor(() => a.Network.Get(b.Id) == null);
        await Task.Delay(3000);
        Assert.Null(a.Network.Get(b.Id));
        Assert.DoesNotContain(a.ConnectedPeers, p => p.Id == b.Id);

        a.Unblock(b.Id);
        Assert.False(a.IsBlocked(b.Id));
        await TestUtil.WaitFor(() => a.Network.Get(b.Id) != null, 30_000);
        var got = new TaskCompletionSource<string>();
        ra.MessageReceived += m => { if (m.From.Id == b.Id) got.TrySetResult(m.Text); };
        await TestUtil.WaitFor(() => rb.Members.Any(m => m.Id == a.Id));
        await rb.SendAsync("welcome back");
        Assert.Equal("welcome back", await got.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_blocked_person_is_left_out_of_your_voice_channel_but_not_everyone_elses()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, c, ra, rb, rc) = await Three(hub);
        using var _ = a; using var __ = b; using var ___ = c;

        using var va = ra.JoinVoice();
        using var vb = rb.JoinVoice();
        using var vc = rc.JoinVoice();
        await TestUtil.WaitFor(() => ra.VoiceParticipants.Count == 3 && rc.VoiceParticipants.Count == 3);

        a.Block(b.Id);
        await TestUtil.WaitFor(() => ra.VoiceParticipants.Count == 2);
        Assert.DoesNotContain(ra.VoiceParticipants, p => p.Peer.Id == b.Id);
        Assert.Contains(rc.VoiceParticipants, p => p.Peer.Id == b.Id);
    }

    [Fact]
    public async Task People_blocked_in_an_earlier_run_stay_blocked()
    {
        var hub = new InMemoryDiscoveryHub();
        using var b = TestUtil.NewNode(hub, "bob");
        using var a = TestUtil.NewNode(hub, "alice", blocked: new[] { b.Id });
        Assert.True(a.IsBlocked(b.Id));
        await a.JoinRoomAsync("lobby");
        await b.JoinRoomAsync("lobby");
        await Task.Delay(3000);
        Assert.Null(a.Network.Get(b.Id));
    }

    [Fact]
    public void You_cannot_block_yourself()
    {
        using var a = TestUtil.NewNode(new InMemoryDiscoveryHub(), "alice");
        Assert.Throws<ArgumentException>(() => a.Block(a.Id));
    }
}
