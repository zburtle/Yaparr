using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

// "Notes to self": the direct chat with your own account, always there, shared between your own devices.
public class NotesToSelfTests : IDisposable
{
    private readonly InMemoryDiscoveryHub _hub = new();
    private readonly PeerIdentity _firstId = PeerIdentity.Generate();
    private readonly PeerIdentity _secondId;
    private readonly List<IDisposable> _nodes = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-notes-" + Guid.NewGuid().ToString("N"));

    public NotesToSelfTests() => _secondId = _firstId.CreateDevice();

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

    [Fact]
    public void Every_account_has_the_chat_first_in_its_stored_chats_even_with_no_note_written()
    {
        var node = Node("alice", _firstId);
        var first = node.StoredDirectChats[0];
        Assert.True(first.IsNotes);
        Assert.Equal(node.AccountId, first.PeerId);
        Assert.Equal(ChatNode.NotesName, first.DisplayName);
        Assert.Null(first.Peer);
        Assert.True(first.IsConnected);
        Assert.Empty(first.History);
    }

    [Fact]
    public async Task Opening_a_chat_with_your_own_id_gives_the_notes_chat_without_looking_for_anyone()
    {
        var node = Node("alice", _firstId);
        var chat = await node.OpenDirectChatAsync(node.AccountId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(chat.IsNotes);
    }

    [Fact]
    public async Task A_note_is_kept_as_your_own_and_does_not_make_the_chat_appear_twice()
    {
        var node = Node("alice", _firstId);
        await node.NotesChat.SendAsync("buy milk");
        var note = Assert.Single(node.NotesChat.History);
        Assert.Equal("buy milk", note.Text);
        Assert.True(node.IsMyDevice(note.From.Id));
        Assert.Equal(1, node.StoredDirectChats.Count(c => c.IsNotes));
    }

    [Fact]
    public async Task A_note_written_here_shows_on_your_other_device_as_your_own_and_never_as_another_person()
    {
        var first = Node("alice", _firstId);
        var second = Node("alice", _secondId);
        var rooms = await Task.WhenAll(first.JoinRoomAsync("lobby"), second.JoinRoomAsync("lobby"));
        await TestUtil.WaitFor(() => first.MyDevices.Count == 2 && second.MyDevices.Count == 2, 30_000);

        var seen = new List<ChatMessage>();
        var onFirst = 0;
        second.NotesChat.MessageReceived += m => { lock (seen) seen.Add(m); };
        first.NotesChat.MessageReceived += _ => Interlocked.Increment(ref onFirst);
        first.IncomingDirectChat += _ => Interlocked.Increment(ref onFirst);

        await first.NotesChat.SendAsync("from my laptop");

        await TestUtil.WaitFor(() => { lock (seen) return seen.Count == 1; });
        await Task.Delay(500);
        lock (seen)
        {
            var m = Assert.Single(seen);
            Assert.Equal("from my laptop", m.Text);
            Assert.True(second.IsMyDevice(m.From.Id));
            Assert.False(m.IsCatchUp);
        }
        // The device that wrote it hears nothing back, and it is no one's direct chat but your own.
        Assert.Equal(0, Volatile.Read(ref onFirst));
        Assert.Single(second.StoredDirectChats);
        Assert.Equal("from my laptop", Assert.Single(second.NotesChat.History).Text);
        // It never counts as unread: nothing in it is from someone else.
        second.NotesChat.MarkRead();
        Assert.Null(second.NotesChat.ReadUpTo);
    }

    [Fact]
    public async Task A_device_that_was_off_gets_the_notes_it_missed_when_it_meets_the_other()
    {
        var first = Node("alice", _firstId);
        await first.NotesChat.SendAsync("one");
        await first.NotesChat.SendAsync("two");

        var second = Node("alice", _secondId);
        var seen = new List<ChatMessage>();
        second.NotesChat.MessageReceived += m => { lock (seen) seen.Add(m); };
        await Task.WhenAll(first.JoinRoomAsync("lobby"), second.JoinRoomAsync("lobby"));

        await TestUtil.WaitFor(() => { lock (seen) return seen.Count == 2; }, 30_000);
        Assert.Equal(new[] { "one", "two" }, second.NotesChat.History.Select(m => m.Text).OrderBy(t => t));
        Assert.Single(second.StoredDirectChats);
    }

    [Fact]
    public async Task Notes_are_kept_across_a_restart()
    {
        using (var alice = TestUtil.NewNode(new InMemoryDiscoveryHub(), "alice", identity: _firstId, directHistoryDirectory: _dir))
            await alice.NotesChat.SendAsync("remember me");

        using var again = TestUtil.NewNode(new InMemoryDiscoveryHub(), "alice", identity: _firstId, directHistoryDirectory: _dir);
        Assert.Equal("remember me", Assert.Single(again.NotesChat.History).Text);
        Assert.Single(again.StoredDirectChats);
    }

    [Fact]
    public void You_cannot_block_yourself_so_the_chat_cannot_be_hidden_that_way()
    {
        var node = Node("alice", _firstId);
        Assert.Throws<ArgumentException>(() => node.Block(node.AccountId));
        Assert.True(node.NotesChat.IsNotes);
    }
}
