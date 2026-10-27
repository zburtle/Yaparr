using System.Collections.Concurrent;
using Yaparr.Chat;
using Yaparr.Discovery;

namespace Yaparr.Tests;

public class DeliveryTests
{
    [Fact]
    public async Task Room_message_is_reported_delivered_once_every_member_confirms()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = TestUtil.NewNode(hub, "a");
        using var b = TestUtil.NewNode(hub, "b");
        using var c = TestUtil.NewNode(hub, "c");
        var ra = await a.JoinRoomAsync("Lobby");
        await b.JoinRoomAsync("Lobby");
        await c.JoinRoomAsync("Lobby");
        await TestUtil.WaitFor(() => ra.Members.Count == 2);

        var seen = new ConcurrentQueue<DeliveryStatus>();
        ra.DeliveryChanged += seen.Enqueue;
        string id = await ra.SendAsync("hello all");

        await TestUtil.WaitFor(() => seen.Any(s => s.MessageId == id && s.State == DeliveryState.Delivered));
        var done = seen.Last(s => s.MessageId == id);
        Assert.Equal(2, done.Total);
        Assert.Equal(2, done.Delivered);
    }

    [Fact]
    public async Task Message_sent_to_an_empty_room_is_queued_and_delivered_when_someone_arrives()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = TestUtil.NewNode(hub, "a");
        using var b = TestUtil.NewNode(hub, "b");
        var ra = await a.JoinRoomAsync("Quiet");

        var seen = new ConcurrentQueue<DeliveryStatus>();
        ra.DeliveryChanged += seen.Enqueue;
        string id = await ra.SendAsync("anyone there?");
        Assert.Contains(seen, s => s.MessageId == id && s.State == DeliveryState.Queued);

        var got = new TaskCompletionSource<ChatMessage>();
        var rb = await b.JoinRoomAsync("Quiet");
        rb.MessageReceived += m => got.TrySetResult(m);
        // Late joiners also get history, so the message may already have arrived before we subscribed.
        await TestUtil.WaitFor(() => seen.Any(s => s.MessageId == id && s.State == DeliveryState.Delivered));
        Assert.Contains("anyone there?", rb.GetHistory().Select(m => m.Text));
    }

    [Fact]
    public async Task Direct_message_is_confirmed_and_not_shown_twice_when_resent()
    {
        var hub = new InMemoryDiscoveryHub();
        using var alice = TestUtil.NewNode(hub, "alice");
        using var bob = TestUtil.NewNode(hub, "bob");
        alice.Start(); bob.Start();
        await Task.Delay(100);

        int bobCount = 0;
        bob.IncomingDirectChat += c => c.MessageReceived += _ => Interlocked.Increment(ref bobCount);
        var toBob = await alice.OpenDirectChatAsync(bob.Id).WaitAsync(TimeSpan.FromSeconds(10));
        var seen = new ConcurrentQueue<DeliveryStatus>();
        toBob.DeliveryChanged += seen.Enqueue;

        string id = await toBob.SendAsync("hi bob");
        await TestUtil.WaitFor(() => seen.Any(s => s.MessageId == id && s.State == DeliveryState.Delivered));

        toBob.Retry(id);
        await Task.Delay(400);
        Assert.Equal(1, bobCount);
    }
}
