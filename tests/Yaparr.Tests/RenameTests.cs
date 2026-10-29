using Yaparr.Chat;
using Yaparr.Discovery;

namespace Yaparr.Tests;

public class RenameTests
{
    [Fact]
    public async Task A_connected_member_sees_the_new_name_at_once_and_in_later_messages()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = TestUtil.NewNode(hub, "alice");
        using var b = TestUtil.NewNode(hub, "bob");
        var ra = await a.JoinRoomAsync("lobby");
        var rb = await b.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => ra.Members.Any(m => m.Id == b.Id) && rb.Members.Any(m => m.Id == a.Id));

        PeerInfo? roomEvent = null, nodeEvent = null;
        ra.MemberRenamed += p => roomEvent = p;
        a.PeerRenamed += p => nodeEvent = p;
        b.Rename("  Bobby  ");

        await TestUtil.WaitFor(() => roomEvent != null && nodeEvent != null);
        Assert.Equal("Bobby", roomEvent!.DisplayName);
        Assert.Equal(b.Id, nodeEvent!.Id);
        Assert.Equal("Bobby", ra.Members.Single(m => m.Id == b.Id).DisplayName);
        Assert.Equal("Bobby", b.DisplayName);

        var got = new TaskCompletionSource<ChatMessage>();
        ra.MessageReceived += m => got.TrySetResult(m);
        await rb.SendAsync("hello");
        Assert.Equal("Bobby", (await got.Task.WaitAsync(TimeSpan.FromSeconds(10))).From.DisplayName);
    }

    [Fact]
    public async Task Someone_you_are_not_connected_to_is_renamed_through_the_room()
    {
        var hub = new InMemoryDiscoveryHub();
        using var middle = TestUtil.NewNode(hub, "middle", relaying: false);
        using var a = TestUtil.NewNode(hub, "alice", relaying: false);
        using var c = TestUtil.NewNode(hub, "carol", relaying: false);
        a.Mux.OutboundFilter = (_, to) => to.Port != c.LocalPort;
        c.Mux.OutboundFilter = (_, to) => to.Port != a.LocalPort;

        await middle.JoinRoomAsync("lobby");
        var ra = await a.JoinRoomAsync("lobby");
        await c.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => ra.Members.Any(m => m.Id == c.Id), 20_000);

        var renamed = new TaskCompletionSource<PeerInfo>();
        ra.MemberRenamed += p => { if (p.Id == c.Id) renamed.TrySetResult(p); };
        c.Rename("Caroline");

        Assert.Equal("Caroline", (await renamed.Task.WaitAsync(TimeSpan.FromSeconds(15))).DisplayName);
        Assert.Equal("Caroline", ra.Members.Single(m => m.Id == c.Id).DisplayName);
    }

    [Fact]
    public async Task A_new_name_shows_in_the_voice_channel()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = TestUtil.NewNode(hub, "alice");
        using var b = TestUtil.NewNode(hub, "bob");
        var ra = await a.JoinRoomAsync("lobby");
        var rb = await b.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => ra.Members.Any(m => m.Id == b.Id) && rb.Members.Any(m => m.Id == a.Id));

        using var va = ra.JoinVoice();
        using var vb = rb.JoinVoice();
        await TestUtil.WaitFor(() => rb.VoiceParticipants.Count == 2);

        a.Rename("Alicia");
        await TestUtil.WaitFor(() => rb.VoiceParticipants.Any(p => p.Peer.Id == a.Id && p.Peer.DisplayName == "Alicia"));
        Assert.Contains(ra.VoiceParticipants, p => p.IsSelf && p.Peer.DisplayName == "Alicia");
    }

    [Fact]
    public void An_empty_name_is_refused_and_a_long_one_is_cut()
    {
        using var node = TestUtil.NewNode(new InMemoryDiscoveryHub(), "alice");
        Assert.Throws<ArgumentException>(() => node.Rename("   "));
        node.Rename(new string('x', 200));
        Assert.Equal(64, node.DisplayName.Length);
    }
}
