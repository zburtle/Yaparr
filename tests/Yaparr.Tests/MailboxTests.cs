using System.Net;
using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

public sealed class MailboxTests
{
    // A server of mailboxes that keeps what it is given the way the real one does: in order, an id stored once, pages of a size it chooses.
    private sealed class FakeMailboxServer : IMailboxTransport
    {
        public readonly Dictionary<string, List<MailboxItemWire>> Boxes = new();
        public int PageSize = 100;
        public int Posts;
        public Exception? FailPost;

        public Task PostAsync(string mailboxId, IReadOnlyList<MailboxPost> posts, CancellationToken cancel = default)
        {
            if (FailPost != null) throw FailPost;
            Posts++;
            if (!Boxes.TryGetValue(mailboxId, out var box)) Boxes[mailboxId] = box = new();
            foreach (var p in posts)
                if (box.All(i => i.Id != p.ItemId)) box.Add(new MailboxItemWire(p.ItemId, p.Body, 0, box.Count == 0 ? 1 : box[^1].Cursor + 1));
            return Task.CompletedTask;
        }

        public Task<MailboxPullResult> PullAsync(string mailboxId, long since, CancellationToken cancel = default)
        {
            var newer = Boxes.GetValueOrDefault(mailboxId)?.Where(i => i.Cursor > since).Take(PageSize + 1).ToList() ?? new();
            bool more = newer.Count > PageSize;
            if (more) newer.RemoveAt(newer.Count - 1);
            return Task.FromResult(new MailboxPullResult(newer.Count > 0 ? newer[^1].Cursor : since, more, newer));
        }
    }

    // Alice has seen Bob in the room and he has gone away, so she knows someone is away. The Bob that is returned is another person's device that joined the same room
    // on a hub of its own, so only the mailbox can carry anything between it and Alice.
    internal static async Task<(ChatNode Alice, ChatRoom AliceRoom, ChatNode Bob, ChatRoom BobRoom, RoomKey Key)> ApartInARoom()
    {
        var hub = new InMemoryDiscoveryHub();
        var alice = TestUtil.NewNode(hub, "alice");
        var first = TestUtil.NewNode(hub, "bob");
        var key = RoomKey.Generate(alice.AccountId);   // a room with an owner keeps a list of who has been in it, which is how a device knows someone is away
        var aliceRoom = await alice.JoinRoomAsync(key, "book club");
        await first.JoinRoomAsync(key, "book club");
        await TestUtil.WaitFor(() => aliceRoom.Members.Count == 1, 20_000);
        first.Dispose();
        await TestUtil.WaitFor(() => aliceRoom.Members.Count == 0, 30_000);   // seen, and now away
        var bob = TestUtil.NewNode(new InMemoryDiscoveryHub(), "bob");
        var bobRoom = await bob.JoinRoomAsync(key, "book club");
        alice.MailboxEnabled = bob.MailboxEnabled = true;
        alice.MailboxWait = bob.MailboxWait = TimeSpan.Zero;
        return (alice, aliceRoom, bob, bobRoom, key);
    }

    [Fact]
    public async Task A_message_reaches_a_member_who_was_never_online_with_the_sender_and_is_news()
    {
        var (alice, aliceRoom, bob, bobRoom, _) = await ApartInARoom();
        using var _1 = alice;
        using var _2 = bob;
        var server = new FakeMailboxServer();
        var got = new List<ChatMessage>();
        bobRoom.MessageReceived += got.Add;

        await aliceRoom.SendAsync("anyone read chapter nine?");
        var sent = await new MailboxSync(alice, server, new MailboxCursors()).SyncAsync();
        Assert.Equal((MailboxSyncResult.Synced, 1), (sent.Result, sent.Posted));

        var read = await new MailboxSync(bob, server, new MailboxCursors()).SyncAsync();
        Assert.Equal(1, read.Received);
        var message = Assert.Single(got);
        Assert.Equal(("anyone read chapter nine?", "alice"), (message.Text, message.From.DisplayName));
        Assert.False(message.IsCatchUp);   // from a few seconds ago: it is how a device that was asleep hears
    }

    [Fact]
    public async Task What_the_server_holds_is_sealed_and_only_the_rooms_members_can_open_it()
    {
        var (alice, aliceRoom, bob, _, key) = await ApartInARoom();
        using var _1 = alice;
        using var _2 = bob;
        var server = new FakeMailboxServer();
        await aliceRoom.SendAsync("the password is hunter2");
        await new MailboxSync(alice, server, new MailboxCursors()).SyncAsync();

        var (id, box) = Assert.Single(server.Boxes);
        Assert.Equal(key.MailboxId, id);
        var item = Assert.Single(box);
        Assert.DoesNotContain("hunter2", item.Body);
        Assert.DoesNotContain("hunter2", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(item.Body)));

        // Another room's key does not open it, nor does a changed item.
        Assert.False(MailboxSeal.TryOpen(RoomKey.Generate(), item.Body, out _));
        var changed = Convert.FromBase64String(item.Body);
        changed[20] ^= 1;
        Assert.False(MailboxSeal.TryOpen(key, Convert.ToBase64String(changed), out _));
        Assert.True(MailboxSeal.TryOpen(key, item.Body, out var opened));
        Assert.Equal("the password is hunter2", opened.Text);
    }

    [Fact]
    public async Task An_item_cannot_be_moved_to_another_mailbox_and_a_forged_one_is_dropped()
    {
        var (alice, aliceRoom, bob, bobRoom, key) = await ApartInARoom();
        using var _1 = alice;
        using var _2 = bob;
        var got = new List<ChatMessage>();
        bobRoom.MessageReceived += got.Add;
        await aliceRoom.SendAsync("hello");
        var post = Assert.Single(alice.TakeMailboxPosts(10));

        // Opened under another mailbox id (the associated data) it fails: sealing is tied to the room.
        var other = RoomKey.Generate();
        Assert.False(MailboxSeal.TryOpen(other, post.Body, out _));

        // Sealed with the right key but a signature that is not the author's: opens, and is dropped by the same check as any message.
        Assert.True(MailboxSeal.TryOpen(key, post.Body, out var real));
        var forged = MailboxSeal.Seal(key, real with { Text = "pay me", Sig = real.Sig });
        Assert.Equal(1, bob.ReceiveFromMailbox(key.MailboxId, new[] { post.Body }));
        Assert.Equal(0, bob.ReceiveFromMailbox(key.MailboxId, new[] { forged }));
        Assert.Equal(new[] { "hello" }, got.Select(m => m.Text));
    }

    [Fact]
    public async Task A_message_comes_once_however_often_it_is_read_and_a_cursor_skips_what_was_read()
    {
        var (alice, aliceRoom, bob, bobRoom, _) = await ApartInARoom();
        using var _1 = alice;
        using var _2 = bob;
        var server = new FakeMailboxServer { PageSize = 2 };
        var got = new List<ChatMessage>();
        bobRoom.MessageReceived += got.Add;
        for (int i = 0; i < 5; i++) await aliceRoom.SendAsync("m" + i);
        var aliceSync = new MailboxSync(alice, server, new MailboxCursors());
        await aliceSync.SyncAsync();
        await aliceSync.SyncAsync();   // nothing new to send

        var cursors = new MailboxCursors();
        var bobSync = new MailboxSync(bob, server, cursors);
        var first = await bobSync.SyncAsync();
        Assert.Equal(5, first.Received);   // three pages of two
        Assert.Equal(5, got.Count);

        var again = await bobSync.SyncAsync();
        Assert.Equal(0, again.Received);   // the cursor is where it stopped
        // Without its cursors (a new install) it reads everything again and the room keeps each message once.
        await new MailboxSync(bob, server, new MailboxCursors()).SyncAsync();
        Assert.Equal(5, got.Count);
    }

    [Fact]
    public async Task A_blocked_authors_message_from_a_mailbox_is_not_shown()
    {
        var (alice, aliceRoom, bob, bobRoom, _) = await ApartInARoom();
        using var _1 = alice;
        using var _2 = bob;
        var server = new FakeMailboxServer();
        var got = new List<ChatMessage>();
        bobRoom.MessageReceived += got.Add;
        bob.Block(alice.Id);
        await aliceRoom.SendAsync("buy my thing");
        await new MailboxSync(alice, server, new MailboxCursors()).SyncAsync();
        await new MailboxSync(bob, server, new MailboxCursors()).SyncAsync();
        Assert.Empty(got);
    }

    [Fact]
    public async Task Nothing_is_queued_unless_it_is_turned_on_and_a_failed_send_is_tried_again()
    {
        var (alice, aliceRoom, bob, _, _) = await ApartInARoom();
        using var _1 = alice;
        using var _2 = bob;
        alice.MailboxEnabled = false;
        await aliceRoom.SendAsync("not queued");
        Assert.Empty(alice.TakeMailboxPosts(10));

        alice.MailboxEnabled = true;
        await aliceRoom.SendAsync("queued");
        var server = new FakeMailboxServer { FailPost = new BackupServerException("Couldn't reach the server.") };
        var sync = new MailboxSync(alice, server, new MailboxCursors());
        await Assert.ThrowsAsync<BackupServerException>(() => sync.SyncAsync());
        Assert.Empty(server.Boxes);

        server.FailPost = null;
        Assert.Equal(1, (await sync.SyncAsync()).Posted);
        Assert.Single(Assert.Single(server.Boxes).Value);
    }

    [Fact]
    public async Task A_mailbox_that_refuses_drops_those_messages_and_the_next_ones_go()
    {
        var (alice, aliceRoom, bob, _, _) = await ApartInARoom();
        using var _1 = alice;
        using var _2 = bob;
        var server = new FakeMailboxServer { FailPost = new BackupServerException("This mailbox already holds 2000 items.", HttpStatusCode.Conflict) };
        await aliceRoom.SendAsync("one");
        var sync = new MailboxSync(alice, server, new MailboxCursors());
        Assert.Equal(0, (await sync.SyncAsync()).Posted);   // dropped, not kept for ever
        server.FailPost = null;
        Assert.Equal(0, (await sync.SyncAsync()).Posted);
        await aliceRoom.SendAsync("two");
        Assert.Equal(1, (await sync.SyncAsync()).Posted);
    }

    [Fact]
    public void A_rooms_mailbox_is_its_own_and_a_replaced_secret_moves_it()
    {
        var owner = PeerIdentity.Generate().Id;
        var one = RoomKey.Generate(owner);
        var two = RoomKey.Generate(owner);
        Assert.Equal(43, one.MailboxId.Length);
        Assert.NotEqual(one.MailboxId, two.MailboxId);
        Assert.Equal(one.MailboxId, RoomKey.Parse(one.ToString()).MailboxId);
        // The owner replaces the secret to lock someone out: the mailbox moves with it, and the key that sealed the old one does not open the new.
        var rotated = one.Rotated(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), 1);
        Assert.NotEqual(one.MailboxId, rotated.MailboxId);
        Assert.NotEqual(Convert.ToHexString(one.MailboxKey), Convert.ToHexString(rotated.MailboxKey));
        Assert.Equal(one.RoomId, rotated.RoomId);
    }
}

public sealed class MailboxPictureTests
{
    private const string Server = "https://cloud.example.org";

    // The pictures part of a server, in memory: it keeps what it is given under the id it is given, as the real one does after it checked the id.
    private sealed class FakeBlobRelay(string server) : IBlobRelay
    {
        public Dictionary<string, byte[]> Held { get; init; } = new();
        public Func<byte[], byte[]>? Tamper;
        public string Server { get; } = server;

        public Task<bool> UploadAsync(string id, byte[] lockedBytes, CancellationToken cancel = default)
        {
            Held[id] = lockedBytes;
            return Task.FromResult(true);
        }

        public Task<byte[]?> DownloadAsync(string id, CancellationToken cancel = default) =>
            Task.FromResult(Held.TryGetValue(id, out var b) ? (Tamper == null ? b : Tamper(b)) : null);
    }

    private static byte[] Png(int padding = 600)
    {
        var b = new byte[40 + padding];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), 64);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), 64);
        System.Security.Cryptography.RandomNumberGenerator.Fill(b.AsSpan(40));
        return b;
    }

    private static async Task<(ChatNode Alice, ChatRoom AliceRoom, ChatNode Bob, ChatRoom BobRoom)> Apart()
    {
        var (alice, aliceRoom, bob, bobRoom, _) = await MailboxTests.ApartInARoom();
        return (alice, aliceRoom, bob, bobRoom);
    }

    // Alice leaves a picture on the server and says so in a message that reaches Bob through the mailbox; returns what Bob received.
    private static async Task<(ChatMessage Message, byte[] Original, FakeBlobRelay Held)> PictureSent(ChatNode alice, ChatRoom aliceRoom, ChatNode bob, ChatRoom bobRoom)
    {
        var held = new FakeBlobRelay(Server);
        alice.BlobRelay = held;
        var original = Png();
        var attachment = await alice.RelayAttachmentAsync(alice.PrepareAttachment(original, "cat.png"), aliceRoom);
        Assert.NotNull(attachment.Relay);
        var got = new List<ChatMessage>();
        bobRoom.MessageReceived += got.Add;
        await aliceRoom.SendAsync(attachment.ToText("look"));
        var mailbox = new MailboxServer();
        await new MailboxSync(alice, mailbox, new MailboxCursors()).SyncAsync();
        await new MailboxSync(bob, mailbox, new MailboxCursors()).SyncAsync();
        return (Assert.Single(got), original, held);
    }

    // A server of mailboxes, as in MailboxTests.
    private sealed class MailboxServer : IMailboxTransport
    {
        private readonly List<MailboxItemWire> _items = new();

        public Task PostAsync(string mailboxId, IReadOnlyList<MailboxPost> posts, CancellationToken cancel = default)
        {
            foreach (var p in posts) _items.Add(new MailboxItemWire(p.ItemId, p.Body, 0, _items.Count + 1));
            return Task.CompletedTask;
        }

        public Task<MailboxPullResult> PullAsync(string mailboxId, long since, CancellationToken cancel = default)
        {
            var newer = _items.Where(i => i.Cursor > since).ToList();
            return Task.FromResult(new MailboxPullResult(newer.Count > 0 ? newer[^1].Cursor : since, false, newer));
        }
    }

    [Fact]
    public async Task A_picture_left_on_the_server_reaches_a_member_who_was_never_online_with_the_sender()
    {
        var (alice, aliceRoom, bob, bobRoom) = await Apart();
        using var _1 = alice;
        using var _2 = bob;
        bob.BlobRelay = null;
        var (message, original, held) = await PictureSent(alice, aliceRoom, bob, bobRoom);
        bob.BlobRelay = new FakeBlobRelay(Server) { Held = held.Held };

        Assert.NotNull(message.Attachment?.Relay);
        Assert.Equal(AttachmentState.Ready, await bob.FetchAttachmentAsync(message));
        Assert.Equal(original, bob.ReadAttachment(message.Attachment!.Hash));
    }

    [Fact]
    public async Task What_the_server_holds_of_a_picture_is_locked_and_has_the_hash_of_its_bytes_as_its_id()
    {
        var (alice, aliceRoom, bob, bobRoom) = await Apart();
        using var _1 = alice;
        using var _2 = bob;
        var (message, original, held) = await PictureSent(alice, aliceRoom, bob, bobRoom);

        var (id, locked) = Assert.Single(held.Held);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(locked)), id);
        Assert.Equal(message.Attachment!.Relay!.Id, id);
        Assert.DoesNotContain(Convert.ToHexString(original[40..80]), Convert.ToHexString(locked));
        Assert.True(locked.Length > original.Length);
    }

    [Fact]
    public async Task A_picture_from_another_server_or_with_changed_bytes_is_not_kept()
    {
        var (alice, aliceRoom, bob, bobRoom) = await Apart();
        using var _1 = alice;
        using var _2 = bob;
        var (message, _, held) = await PictureSent(alice, aliceRoom, bob, bobRoom);

        // Signed in to another server than the one it was left on: not fetched, so a message cannot make the app contact a server of the sender's choosing.
        bob.BlobRelay = new FakeBlobRelay("https://other.example.net") { Held = held.Held };
        Assert.NotEqual(AttachmentState.Ready, await bob.FetchAttachmentAsync(message, force: true));
        Assert.False(bob.HasAttachment(message.Attachment!.Hash));

        // The right server, but what comes back was changed on the way.
        bob.BlobRelay = new FakeBlobRelay(Server) { Held = held.Held, Tamper = b => { var c = (byte[])b.Clone(); c[^1] ^= 1; return c; } };
        Assert.NotEqual(AttachmentState.Ready, await bob.FetchAttachmentAsync(message, force: true));
        Assert.False(bob.HasAttachment(message.Attachment!.Hash));
    }

    [Fact]
    public async Task Without_a_server_the_picture_is_sent_as_before_and_a_malformed_locked_copy_is_ignored()
    {
        var node = TestUtil.NewNode(new InMemoryDiscoveryHub(), "alice");
        using var disposeNode = node;
        var plain = node.PrepareAttachment(Png(), "a.png");
        Assert.Same(plain, await node.RelayAttachmentAsync(plain));   // no server: unchanged

        var relayed = plain with { Relay = new BlobRef(Server, new string('a', 64), Convert.ToBase64String(new byte[32])) };
        Assert.True(Attachment.TryParse(relayed.ToText("x"), out var back, out _));
        Assert.Equal(relayed.Relay, back.Relay);
        var bad = plain with { Relay = new BlobRef("http://cloud.example.org", new string('a', 64), Convert.ToBase64String(new byte[32])) };
        Assert.True(Attachment.TryParse(bad.ToText("x"), out var ignored, out _));
        Assert.Null(ignored.Relay);   // not https: left out, the picture itself still shows
    }
}

// Direct messages: the two people share a secret of their own, made when they first talk, so a message written while the other is away can wait in a mailbox.
public sealed class MailboxDirectTests : IDisposable
{
    private readonly InMemoryDiscoveryHub _hub = new();
    private readonly PeerIdentity _aliceId = PeerIdentity.Generate();
    private readonly PeerIdentity _bobId = PeerIdentity.Generate();
    private readonly List<IDisposable> _nodes = new();

    public void Dispose()
    {
        foreach (var n in _nodes) n.Dispose();
        _aliceId.Dispose();
        _bobId.Dispose();
    }

    private T Keep<T>(T node) where T : IDisposable { _nodes.Add(node); return node; }

    private sealed class Server : IMailboxTransport
    {
        public readonly List<(string Box, MailboxItemWire Item)> Items = new();

        public Task PostAsync(string mailboxId, IReadOnlyList<MailboxPost> posts, CancellationToken cancel = default)
        {
            foreach (var p in posts) if (!Items.Any(i => i.Box == mailboxId && i.Item.Id == p.ItemId)) Items.Add((mailboxId, new MailboxItemWire(p.ItemId, p.Body, 0, Items.Count + 1)));
            return Task.CompletedTask;
        }

        public Task<MailboxPullResult> PullAsync(string mailboxId, long since, CancellationToken cancel = default)
        {
            var newer = Items.Where(i => i.Box == mailboxId && i.Item.Cursor > since).Select(i => i.Item).ToList();
            return Task.FromResult(new MailboxPullResult(newer.Count > 0 ? newer[^1].Cursor : since, false, newer));
        }
    }

    // Alice and Bob talk once (so each has the conversation's secret), then Bob goes away; returns Bob's account settings as they were, for the device that comes back.
    private async Task<(ChatNode Alice, DirectChat ToBob, IReadOnlyList<AccountEntry> BobEntries)> TalkedAndBobLeft()
    {
        var alice = Keep(TestUtil.NewNode(_hub, "alice", identity: _aliceId));
        var bob = Keep(TestUtil.NewNode(_hub, "bob", identity: _bobId));
        alice.MailboxEnabled = bob.MailboxEnabled = true;
        alice.MailboxWait = bob.MailboxWait = TimeSpan.Zero;
        alice.Start(); bob.Start();
        var chat = await alice.OpenDirectChatAsync(bob.AccountId).WaitAsync(TimeSpan.FromSeconds(10));
        await chat.SendAsync("hello bob");
        await TestUtil.WaitFor(() => bob.GetDirectChat(alice.AccountId).History.Any(m => m.Text == "hello bob"), 20_000);
        // Bob is connected and has it, so nothing is left on the server for it.
        Assert.Empty(alice.TakeMailboxPosts(10));
        // Both hold the same secret now, so both compute the same mailbox.
        await TestUtil.WaitFor(() => alice.MailboxIds().Intersect(bob.MailboxIds()).Any(), 10_000);
        var entries = bob.AccountEntries;
        bob.Dispose();
        _nodes.Remove(bob);
        await TestUtil.WaitFor(() => !chat.IsConnected, 30_000);
        return (alice, chat, entries);
    }

    [Fact]
    public async Task A_direct_message_written_while_the_other_is_away_reaches_them_through_the_mailbox_on_another_connection()
    {
        var (alice, chat, bobEntries) = await TalkedAndBobLeft();
        var server = new Server();
        await chat.SendAsync("while you were away");
        // The first message went while Bob was connected and is not left; this one is, because he is away.
        Assert.Equal(1, (await new MailboxSync(alice, server, new MailboxCursors()).SyncAsync()).Posted);

        // Bob comes back on a device that never met Alice's: it has Bob's account settings (so the secret) and nothing else.
        var bob2 = Keep(TestUtil.NewNode(new InMemoryDiscoveryHub(), "bob", identity: _bobId, accountEntries: bobEntries));
        bob2.MailboxEnabled = true;
        var got = new List<ChatMessage>();
        bob2.GetDirectChat(alice.AccountId).MessageReceived += got.Add;
        var read = await new MailboxSync(bob2, server, new MailboxCursors()).SyncAsync();

        Assert.Equal(1, read.Received);
        var message = Assert.Single(got, m => m.Text == "while you were away");
        Assert.False(message.IsCatchUp);
        Assert.Contains(bob2.GetDirectChat(alice.AccountId).History, m => m.Text == "while you were away");

        // Read again, it is not shown twice.
        await new MailboxSync(bob2, server, new MailboxCursors()).SyncAsync();
        Assert.Single(bob2.GetDirectChat(alice.AccountId).History, m => m.Text == "while you were away");
    }

    [Fact]
    public async Task What_the_server_holds_of_a_direct_message_is_locked_and_others_cannot_make_one()
    {
        var (alice, chat, bobEntries) = await TalkedAndBobLeft();
        var server = new Server();
        await chat.SendAsync("the code is 4417");
        await new MailboxSync(alice, server, new MailboxCursors()).SyncAsync();

        Assert.Single(server.Items);
        var (box, item) = server.Items[^1];
        Assert.DoesNotContain("4417", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(item.Body)));

        // A device of some third person holds neither the secret nor the mailbox, and a made-up item for the right mailbox does not open for it.
        var carol = Keep(TestUtil.NewNode(new InMemoryDiscoveryHub(), "carol"));
        carol.MailboxEnabled = true;
        Assert.Equal(0, carol.ReceiveFromMailbox(box, new[] { item.Body }));
        var bob2 = Keep(TestUtil.NewNode(new InMemoryDiscoveryHub(), "bob", identity: _bobId, accountEntries: bobEntries));
        Assert.Equal(0, bob2.ReceiveFromMailbox(box, new[] { Convert.ToBase64String(new byte[80]) }));
        Assert.Empty(bob2.GetDirectChat(alice.AccountId).History);
    }

    [Fact]
    public async Task Nothing_is_queued_for_a_direct_message_unless_it_is_turned_on()
    {
        var alice = Keep(TestUtil.NewNode(_hub, "alice", identity: _aliceId));
        var bob = Keep(TestUtil.NewNode(_hub, "bob", identity: _bobId));
        alice.Start(); bob.Start();
        var chat = await alice.OpenDirectChatAsync(bob.AccountId).WaitAsync(TimeSpan.FromSeconds(10));
        await chat.SendAsync("hello");
        Assert.Empty(alice.TakeMailboxPosts(10));
        Assert.Empty(alice.MailboxIds());
    }
}

// The server only holds what someone known to be away has not received: nothing is left when everyone is connected, or when the person turned it off.
public sealed class MailboxWhenAwayTests
{
    private sealed class Relay(string server) : IBlobRelay
    {
        public int Uploads;
        public string Server { get; } = server;
        public Task<bool> UploadAsync(string id, byte[] lockedBytes, CancellationToken cancel = default) { Uploads++; return Task.FromResult(true); }
        public Task<byte[]?> DownloadAsync(string id, CancellationToken cancel = default) => Task.FromResult<byte[]?>(null);
    }

    private static byte[] Png()
    {
        var b = new byte[640];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), 64);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), 64);
        System.Security.Cryptography.RandomNumberGenerator.Fill(b.AsSpan(40));
        return b;
    }

    // Alice and Bob are both here, in one room.
    private static async Task<(ChatNode Alice, ChatRoom AliceRoom, ChatNode Bob)> BothConnected()
    {
        var hub = new InMemoryDiscoveryHub();
        var alice = TestUtil.NewNode(hub, "alice");
        var bob = TestUtil.NewNode(hub, "bob");
        var key = RoomKey.Generate(alice.AccountId);
        var aliceRoom = await alice.JoinRoomAsync(key, "book club");
        await bob.JoinRoomAsync(key, "book club");
        await TestUtil.WaitFor(() => aliceRoom.Members.Count == 1, 20_000);
        alice.MailboxEnabled = true;
        alice.MailboxWait = TimeSpan.Zero;
        return (alice, aliceRoom, bob);
    }

    [Fact]
    public async Task Nothing_is_left_on_the_server_when_everyone_known_is_connected()
    {
        var (alice, room, bob) = await BothConnected();
        using var _1 = alice;
        using var _2 = bob;
        await room.SendAsync("everyone is here");
        Assert.Empty(alice.TakeMailboxPosts(10));
    }

    [Fact]
    public async Task A_message_is_left_when_the_wait_is_over_and_someone_who_was_here_has_gone()
    {
        var (alice, room, bob) = await BothConnected();
        using var _1 = alice;
        await room.SendAsync("one more thing");
        bob.Dispose();
        await TestUtil.WaitFor(() => room.Members.Count == 0, 30_000);
        Assert.Single(alice.TakeMailboxPosts(10));   // the wait is over, and now someone known is away
    }

    [Fact]
    public async Task A_message_waits_its_turn_before_it_is_left()
    {
        var (alice, room, bob) = await BothConnected();
        using var _1 = alice;
        bob.Dispose();
        await TestUtil.WaitFor(() => room.Members.Count == 0, 30_000);
        alice.MailboxWait = TimeSpan.FromMinutes(5);
        await room.SendAsync("not yet");
        Assert.Empty(alice.TakeMailboxPosts(10));    // still inside the wait: it may yet reach him another way
    }

    [Fact]
    public async Task Nothing_is_left_when_the_person_turned_leaving_off()
    {
        var (alice, room, bob) = await BothConnected();
        using var _1 = alice;
        bob.Dispose();
        await TestUtil.WaitFor(() => room.Members.Count == 0, 30_000);
        alice.MailboxLeaving = false;
        await room.SendAsync("keep this between us");
        Assert.Empty(alice.TakeMailboxPosts(10));
    }

    [Fact]
    public async Task A_picture_is_only_uploaded_when_someone_is_away_and_leaving_is_on()
    {
        var (alice, room, bob) = await BothConnected();
        using var _1 = alice;
        var relay = new Relay("https://cloud.example.org");
        alice.BlobRelay = relay;

        var everyoneHere = await alice.RelayAttachmentAsync(alice.PrepareAttachment(Png(), "a.png"), room);
        Assert.Null(everyoneHere.Relay);
        Assert.Equal(0, relay.Uploads);

        bob.Dispose();
        await TestUtil.WaitFor(() => room.Members.Count == 0, 30_000);
        alice.MailboxLeaving = false;
        Assert.Null((await alice.RelayAttachmentAsync(alice.PrepareAttachment(Png(), "b.png"), room)).Relay);
        Assert.Equal(0, relay.Uploads);

        alice.MailboxLeaving = true;
        var away = await alice.RelayAttachmentAsync(alice.PrepareAttachment(Png(), "c.png"), room);
        Assert.NotNull(away.Relay);
        Assert.Equal(1, relay.Uploads);
    }
}
