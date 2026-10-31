using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

// Two devices of one person show the same direct conversations: sent and received, live and after being off.
public class DirectSyncTests : IDisposable
{
    private readonly InMemoryDiscoveryHub _hub = new();
    private readonly PeerIdentity _firstId = PeerIdentity.Generate();
    private readonly PeerIdentity _secondId;
    private readonly List<IDisposable> _nodes = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-dm-" + Guid.NewGuid().ToString("N"));

    public DirectSyncTests() => _secondId = _firstId.CreateDevice();

    public void Dispose()
    {
        foreach (var n in _nodes) n.Dispose();
        _firstId.Dispose(); _secondId.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private ChatNode Node(string name, PeerIdentity? identity = null, string? dmDir = null)
    {
        var node = TestUtil.NewNode(_hub, name, identity: identity, directHistoryDirectory: dmDir);
        _nodes.Add(node);
        return node;
    }

    // Alice on two devices and Bob, all in one room so they meet.
    private async Task<(ChatNode first, ChatNode second, ChatNode bob)> StartAsync()
    {
        var first = Node("alice", _firstId);
        var second = Node("alice", _secondId);
        var bob = Node("bob");
        var rooms = await Task.WhenAll(first.JoinRoomAsync("lobby"), second.JoinRoomAsync("lobby"), bob.JoinRoomAsync("lobby"));
        await TestUtil.WaitFor(() => rooms.All(r => r.Members.Count == 2));
        return (first, second, bob);
    }

    [Fact]
    public async Task A_message_sent_from_one_device_appears_as_your_own_on_the_other()
    {
        var (first, second, bob) = await StartAsync();
        var seen = new List<ChatMessage>();
        PeerId? chatPeer = null;
        second.IncomingDirectChat += c => { chatPeer = c.PeerId; c.MessageReceived += m => { lock (seen) seen.Add(m); }; };

        var chat = await first.OpenDirectChatAsync(bob.AccountId);
        await chat.SendAsync("from my laptop");

        await TestUtil.WaitFor(() => { lock (seen) return seen.Count == 1; });
        var m = seen[0];
        Assert.Equal("from my laptop", m.Text);
        Assert.Equal(bob.AccountId, chatPeer);
        Assert.True(second.IsMyDevice(m.From.Id));
        Assert.False(m.IsCatchUp);
    }

    [Fact]
    public async Task A_message_that_reaches_both_devices_directly_and_by_copy_is_shown_once_on_each()
    {
        var (first, second, bob) = await StartAsync();
        int onFirst = 0, onSecond = 0;
        first.IncomingDirectChat += c => c.MessageReceived += _ => Interlocked.Increment(ref onFirst);
        second.IncomingDirectChat += c => c.MessageReceived += _ => Interlocked.Increment(ref onSecond);

        var chat = await bob.OpenDirectChatAsync(first.AccountId);
        await chat.SendAsync("one");
        await TestUtil.WaitFor(() => Volatile.Read(ref onFirst) >= 1 && Volatile.Read(ref onSecond) >= 1);
        await Task.Delay(800);   // time for the copies to cross

        Assert.Equal(1, Volatile.Read(ref onFirst));
        Assert.Equal(1, Volatile.Read(ref onSecond));
    }

    [Fact]
    public async Task A_device_that_was_off_gets_the_conversations_it_missed_from_the_other()
    {
        var first = Node("alice", _firstId);
        var bob = Node("bob");
        var rooms = await Task.WhenAll(first.JoinRoomAsync("lobby"), bob.JoinRoomAsync("lobby"));
        await TestUtil.WaitFor(() => rooms.All(r => r.Members.Count == 1));

        var toBob = await first.OpenDirectChatAsync(bob.AccountId);
        await toBob.SendAsync("hello bob");
        await TestUtil.WaitFor(() => first.StoredDirectChats.Count == 1);
        var toAlice = await bob.OpenDirectChatAsync(first.AccountId);
        await toAlice.SendAsync("hello alice");
        await TestUtil.WaitFor(() => first.GetDirectChat(bob.AccountId).History.Count == 2);

        var second = Node("alice", _secondId);
        var seen = new List<ChatMessage>();
        second.IncomingDirectChat += c => c.MessageReceived += m => { lock (seen) seen.Add(m); };
        await second.JoinRoomAsync("lobby");

        await TestUtil.WaitFor(() => { lock (seen) return seen.Count == 2; });
        lock (seen)
        {
            Assert.Contains(seen, m => m.Text == "hello bob" && second.IsMyDevice(m.From.Id));
            Assert.Contains(seen, m => m.Text == "hello alice" && !second.IsMyDevice(m.From.Id) && m.IsCatchUp);
        }
        Assert.Equal(2, second.GetDirectChat(bob.AccountId).History.Count);
    }

    [Fact]
    public async Task Conversations_are_kept_across_a_restart_when_a_folder_is_given()
    {
        var hub = new InMemoryDiscoveryHub();
        using (var alice = TestUtil.NewNode(hub, "alice", identity: _firstId, directHistoryDirectory: _dir))
        using (var bob = TestUtil.NewNode(hub, "bob"))
        {
            var rooms = await Task.WhenAll(alice.JoinRoomAsync("lobby"), bob.JoinRoomAsync("lobby"));
            await TestUtil.WaitFor(() => rooms.All(r => r.Members.Count == 1));
            var chat = await alice.OpenDirectChatAsync(bob.AccountId);
            await chat.SendAsync("remember me");
            await TestUtil.WaitFor(() => alice.GetDirectChat(bob.AccountId).History.Count == 1);
        }

        using var again = TestUtil.NewNode(new InMemoryDiscoveryHub(), "alice", identity: _firstId, directHistoryDirectory: _dir);
        var stored = Assert.Single(again.StoredDirectChats);
        var message = Assert.Single(stored.History);
        Assert.Equal("remember me", message.Text);
        Assert.True(again.IsMyDevice(message.From.Id));
    }

    [Fact]
    public async Task Reading_a_conversation_on_one_device_is_known_on_the_other()
    {
        var (first, second, bob) = await StartAsync();
        DirectChat? onFirst = null, onSecond = null;
        first.IncomingDirectChat += c => onFirst = c;
        second.IncomingDirectChat += c => onSecond = c;
        var chat = await bob.OpenDirectChatAsync(first.AccountId);
        await chat.SendAsync("did you see this?");
        await TestUtil.WaitFor(() => onFirst != null && onSecond != null);
        Assert.Null(onSecond!.ReadUpTo);

        int raised = 0;
        onSecond.ReadChanged += () => Interlocked.Increment(ref raised);
        onFirst!.MarkRead();

        await TestUtil.WaitFor(() => Volatile.Read(ref raised) == 1);
        Assert.NotNull(onSecond.ReadUpTo);
        Assert.Equal(onFirst.ReadUpTo, onSecond.ReadUpTo);

        onFirst.MarkRead();   // nothing newer to read: no further change
        await Task.Delay(300);
        Assert.Equal(1, Volatile.Read(ref raised));
    }
}
