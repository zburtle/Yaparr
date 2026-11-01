using System.Collections.Concurrent;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

// A direct message written while the other person is away waits and is sent when they are back, also after a restart.
public class DirectQueueTests : IDisposable
{
    private readonly InMemoryDiscoveryHub _hub = new();
    private readonly PeerIdentity _aliceId = PeerIdentity.Generate();
    private readonly PeerIdentity _bobId = PeerIdentity.Generate();
    private readonly string _aliceDir = Path.Combine(Path.GetTempPath(), "yaparr-dmq-a-" + Guid.NewGuid().ToString("N"));
    private readonly string _bobDir = Path.Combine(Path.GetTempPath(), "yaparr-dmq-b-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _nodes = new();

    public void Dispose()
    {
        foreach (var n in _nodes) n.Dispose();
        _aliceId.Dispose(); _bobId.Dispose();
        foreach (var dir in new[] { _aliceDir, _bobDir }) { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private ChatNode Alice() => Keep(TestUtil.NewNode(_hub, "alice", identity: _aliceId, directHistoryDirectory: _aliceDir));

    private ChatNode Bob() => Keep(TestUtil.NewNode(_hub, "bob", identity: _bobId, directHistoryDirectory: _bobDir));

    private ChatNode Keep(ChatNode node) { _nodes.Add(node); return node; }

    private void Stop(ChatNode node) { node.Dispose(); _nodes.Remove(node); }

    // Alice and Bob have talked, so there is a conversation to add to, and then Bob goes away.
    private async Task<(ChatNode Alice, DirectChat ToBob)> TalkedAndBobLeftAsync()
    {
        var alice = Alice();
        var bob = Bob();
        alice.Start(); bob.Start();
        var chat = await alice.OpenDirectChatAsync(bob.AccountId).WaitAsync(TimeSpan.FromSeconds(10));
        var first = await chat.SendAsync("hello bob");
        var seen = new ConcurrentQueue<DeliveryStatus>();
        chat.DeliveryChanged += seen.Enqueue;
        await TestUtil.WaitFor(() => bob.GetDirectChat(alice.AccountId).History.Any(m => m.Text == "hello bob"), 20_000);
        Stop(bob);
        await TestUtil.WaitFor(() => !chat.IsConnected, 30_000);
        return (alice, chat);
    }

    [Fact]
    public async Task A_message_to_someone_who_is_away_waits_and_arrives_when_they_are_back()
    {
        var (alice, chat) = await TalkedAndBobLeftAsync();
        var seen = new ConcurrentQueue<DeliveryStatus>();
        chat.DeliveryChanged += seen.Enqueue;

        var id = await chat.SendAsync("while you were away");
        Assert.Contains(seen, s => s.MessageId == id && s.State == DeliveryState.Queued);
        Assert.Contains(chat.History, m => m.Text == "while you were away");

        var bob = Bob();
        bob.Start();
        await alice.OpenDirectChatAsync(bob.AccountId).WaitAsync(TimeSpan.FromSeconds(20));
        await TestUtil.WaitFor(() => bob.GetDirectChat(alice.AccountId).History.Any(m => m.Text == "while you were away"), 30_000);
        await TestUtil.WaitFor(() => seen.Any(s => s.MessageId == id && s.State == DeliveryState.Delivered), 30_000);
        // Once, not once per reconnection.
        Assert.Single(bob.GetDirectChat(alice.AccountId).History, m => m.Text == "while you were away");
    }

    [Fact]
    public async Task A_waiting_message_survives_a_restart_of_the_sender()
    {
        var (alice, chat) = await TalkedAndBobLeftAsync();
        await chat.SendAsync("still waiting");
        Stop(alice);
        Assert.True(File.Exists(Path.Combine(_aliceDir, "pending.json")));

        var again = Alice();
        again.Start();
        Assert.Contains(again.GetDirectChat(_bobId.AccountId).History, m => m.Text == "still waiting");

        var bob = Bob();
        bob.Start();
        await again.OpenDirectChatAsync(bob.AccountId).WaitAsync(TimeSpan.FromSeconds(20));
        await TestUtil.WaitFor(() => bob.GetDirectChat(again.AccountId).History.Any(m => m.Text == "still waiting"), 30_000);
        // Delivered, so it is no longer waiting.
        await TestUtil.WaitFor(() => !File.ReadAllText(Path.Combine(_aliceDir, "pending.json")).Contains("\"id\""), 30_000);
    }

    [Fact]
    public async Task A_first_message_to_someone_not_connected_is_still_refused()
    {
        var alice = Alice();
        var stranger = new PeerIdentity[] { PeerIdentity.Generate() };
        using var _ = stranger[0];
        var chat = alice.GetDirectChat(stranger[0].AccountId);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await chat.SendAsync("hello?"));
    }
}
