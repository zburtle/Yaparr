using System.Net;
using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

// The owner of a room writes a signed start record when it is made. Anyone who holds it knows the room began then, and that reading back has reached the beginning once
// nothing earlier is held; it is never shown, and nobody but the owner can make one.
public class RoomStartTests
{
    private static ChatNode Node(InMemoryDiscoveryHub hub, string name, PeerIdentity identity, IReadOnlyList<AccountEntry>? entries = null) => new(new ChatNodeOptions
    {
        DisplayName = name,
        Identity = identity,
        AccountEntries = entries ?? Array.Empty<AccountEntry>(),
        DiscoveryFactory = mux => hub.CreateClient(new IPEndPoint(IPAddress.Loopback, mux.LocalPort)),
        DiscoveryInterval = TimeSpan.FromMilliseconds(300),
        Network = TestUtil.FastNetwork,
    });

    private static int StartRecordsHeld(ChatRoom room) => room.History.Before(RoomChannels.StartMarker, long.MaxValue, "", 50).Count;

    [Fact]
    public void A_start_record_proves_whose_it_is_and_nothing_else_looks_like_one()
    {
        using var owner = PeerIdentity.Generate();
        var text = MessageEnvelope.WrapStart(owner);
        Assert.True(MessageEnvelope.IsStartText(text));
        Assert.True(MessageEnvelope.TryReadStart(text, out var account, out var certificate));
        Assert.Null(account);       // the account's own key: nothing more to prove
        Assert.Null(certificate);

        Assert.False(MessageEnvelope.IsStartText("started this room"));
        Assert.False(MessageEnvelope.IsStartText(MessageEnvelope.WrapReaction(new string('a', 32), "👍", true)));
        Assert.False(MessageEnvelope.TryReadStart("\u0001yaparr-env1\u0001{\"s\":}\u0001x", out _, out _));
    }

    [Fact]
    public async Task The_owner_writes_one_record_when_the_room_is_made_and_a_member_who_joins_later_has_it_without_it_being_shown()
    {
        var hub = new InMemoryDiscoveryHub();
        using var aliceId = PeerIdentity.Generate();
        using var bobId = PeerIdentity.Generate();
        using var alice = Node(hub, "alice", aliceId);
        using var bob = Node(hub, "bob", bobId);
        var key = RoomKey.Generate(alice.AccountId);

        var ra = await alice.JoinRoomAsync(key, "Home");
        Assert.Null(ra.StartedAt);
        ra.RecordStart();
        Assert.NotNull(ra.StartedAt);
        ra.RecordStart();                      // once only
        Assert.Equal(1, StartRecordsHeld(ra));
        await ra.SendAsync("hello");
        await ra.SendAsync("how are you");

        var rb = await bob.JoinRoomAsync(key, "Home");
        var received = new List<string>();
        rb.MessageReceived += m => { lock (received) received.Add(m.Text); };
        await TestUtil.WaitFor(() => rb.StartedAt != null, 30_000);
        Assert.InRange((DateTimeOffset.UtcNow - rb.StartedAt!.Value).TotalSeconds, -5, 120);

        // It came as history but is no message: not shown, not announced, not in the history read back.
        await TestUtil.WaitFor(() => rb.GetHistory().Count >= 2, 30_000);
        Assert.Equal(new[] { "hello", "how are you" }, rb.GetHistory().Select(m => m.Text).ToArray());
        lock (received) Assert.DoesNotContain(received, t => MessageEnvelope.IsStartText(t) || t.Contains("started this room"));

        // Nothing is earlier than the first message, and the start is held: the beginning, for sure.
        var first = rb.GetHistory()[0];
        var page = rb.GetEarlierHistory(null, first.Key, 100);
        Assert.Empty(page.Messages);
        Assert.False(page.HasMore);
        Assert.True(page.AtStart);
    }

    [Fact]
    public async Task Without_a_start_record_the_end_of_the_history_is_not_known_for_sure()
    {
        var hub = new InMemoryDiscoveryHub();
        using var aliceId = PeerIdentity.Generate();
        using var alice = Node(hub, "alice", aliceId);
        var ra = await alice.JoinRoomAsync(RoomKey.Generate(alice.AccountId), "Old room");   // made without one, as a room from before
        await ra.SendAsync("hello");

        Assert.Null(ra.StartedAt);
        var page = ra.GetEarlierHistory(null, ra.GetHistory()[0].Key, 100);
        Assert.False(page.HasMore);
        Assert.False(page.AtStart);   // nothing earlier here, but that is all that can be said
    }

    [Fact]
    public async Task Only_the_owner_can_make_one_and_a_room_with_no_owner_has_none()
    {
        var hub = new InMemoryDiscoveryHub();
        using var aliceId = PeerIdentity.Generate();
        using var bobId = PeerIdentity.Generate();
        using var malloryId = PeerIdentity.Generate();
        using var bob = Node(hub, "bob", bobId);
        using var mallory = Node(hub, "mallory", malloryId);
        var key = RoomKey.Generate(aliceId.AccountId);   // alice owns it and is not here

        var rb = await bob.JoinRoomAsync(key, "Home");
        var rm = await mallory.JoinRoomAsync(key, "Home");
        await TestUtil.WaitFor(() => rb.Members.Count == 1 && rm.Members.Count == 1, 30_000);

        rm.RecordStart();   // not the owner: nothing is written
        Assert.Equal(0, StartRecordsHeld(rm));

        // A member who forges one anyway (the message is signed by her, but she is not the owner) is kept like any message and counts for nothing.
        mallory.SendRoomMessage(rm, MessageEnvelope.WrapStart(malloryId), RoomChannels.StartMarker);
        await TestUtil.WaitFor(() => StartRecordsHeld(rb) == 1, 30_000);
        Assert.Null(rb.StartedAt);
        Assert.Null(rm.StartedAt);

        // A room with no owner has no start at all.
        var open = await bob.JoinRoomAsync(RoomKey.Generate(), "Open");
        open.RecordStart();
        Assert.Equal(0, StartRecordsHeld(open));
        Assert.Null(open.StartedAt);
    }

    [Fact]
    public async Task A_record_written_on_a_linked_device_of_the_owner_counts_for_the_owner_on_every_device()
    {
        var hub = new InMemoryDiscoveryHub();
        using var computerId = PeerIdentity.Generate();
        using var computer = Node(hub, "alice", computerId);
        var pass = DeviceLink.GeneratePassphrase();
        var linked = DeviceLink.Open(DeviceLink.Create(computerId, computer.DisplayName, computer.AccountEntries, pass), pass);
        using var phone = Node(hub, "alice", linked.Identity, linked.Entries);
        using var bobId = PeerIdentity.Generate();
        using var bob = Node(hub, "bob", bobId);
        var key = RoomKey.Generate(computer.AccountId);

        // The phone, a device of the owner's, makes the room: the record carries the proof of whose it is.
        var onPhone = await phone.JoinRoomAsync(key, "Home");
        onPhone.RecordStart();
        Assert.NotNull(onPhone.StartedAt);

        var rb = await bob.JoinRoomAsync(key, "Home");
        await TestUtil.WaitFor(() => rb.StartedAt != null, 30_000);   // Bob has never met the phone, and can still tell whose it is
    }
}
