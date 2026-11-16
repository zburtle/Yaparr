using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.History.Sqlite;
using Yaparr.Identity;

namespace Yaparr.Tests;

// History is kept for good and read a page at a time.
public class HistoryPagingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-paging-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static ChatNode NodeWith(InMemoryDiscoveryHub hub, PeerIdentity identity, IChatHistoryStore store) => new(new ChatNodeOptions
    {
        DisplayName = "me", Identity = identity, HistoryStore = store, SyncHistory = false,
        DiscoveryFactory = mux => hub.CreateClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, mux.LocalPort)),
        DiscoveryInterval = TimeSpan.FromMilliseconds(300), Network = TestUtil.FastNetwork,
    });

    [Fact]
    public async Task A_room_opens_with_its_newest_hundred_and_earlier_messages_are_read_a_page_at_a_time()
    {
        using var identity = PeerIdentity.Generate();
        using var store = new SqliteHistoryStore(Path.Combine(_dir, "history.db"), new byte[32].Select((_, i) => (byte)(i + 3)).ToArray());
        using (var first = NodeWith(new InMemoryDiscoveryHub(), identity, store))
        {
            var room = await first.JoinRoomAsync("long");
            for (int i = 0; i < 250; i++) await room.SendAsync($"message {i:000}");
        }

        using var second = NodeWith(new InMemoryDiscoveryHub(), identity, store);
        var again = await second.JoinRoomAsync("long");
        var opened = again.GetHistory();
        Assert.Equal(100, opened.Count);
        Assert.Equal("message 150", opened[0].Text);
        Assert.Equal("message 249", opened[^1].Text);

        var page = again.GetEarlierHistory(null, opened[0].Key, 100);
        Assert.True(page.HasMore);
        Assert.Equal(100, page.Messages.Count);
        Assert.Equal("message 050", page.Messages[0].Text);
        Assert.Equal("message 149", page.Messages[^1].Text);

        var last = again.GetEarlierHistory(null, page.Messages[0].Key, 100);
        Assert.False(last.HasMore);
        Assert.Equal(50, last.Messages.Count);
        Assert.Equal("message 000", last.Messages[0].Text);
        Assert.Empty(again.GetEarlierHistory(null, last.Messages[0].Key, 100).Messages);
    }

    [Fact]
    public async Task Messages_that_are_old_are_still_there_and_reactions_come_for_the_messages_asked_about()
    {
        using var identity = PeerIdentity.Generate();
        using var store = new SqliteHistoryStore(null);
        using var node = NodeWith(new InMemoryDiscoveryHub(), identity, store);
        var room = await node.JoinRoomAsync("react");
        for (int i = 0; i < 150; i++) await room.SendAsync($"m{i:000}");
        var shown = room.GetHistory();
        var oldest = shown[0];
        var older = room.GetEarlierHistory(null, oldest.Key, 10).Messages[^1];
        await room.SendReactionAsync(older.Key, "👍", true);

        // The reaction is on a message that is not among those shown at first, so it is not counted until that message is asked about.
        Assert.Empty(room.GetReactions().For(older.Key));
        Assert.Single(room.GetReactions(new[] { older.Key }).For(older.Key));
    }

    [Fact]
    public void The_history_files_of_earlier_versions_are_brought_over_and_removed()
    {
        using var identity = PeerIdentity.Generate();
        var cipher = new HistoryCipher(new byte[32].Select((_, i) => (byte)(i + 9)).ToArray());
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var files = new FileHistoryStore(_dir, cipher);
        var good = Enumerable.Range(0, 5).Select(i => SignedMessage.Create(identity, "roomA", "x", $"old {i}", now - (5 - i) * 1000)).ToList();
        var forged = SignedMessage.Create(identity, "roomA", "x", "forged", now) with { Text = "changed after signing" };
        foreach (var m in good.Append(forged)) files.Append("roomA", m);
        files.Append("roomB", SignedMessage.Create(identity, "roomB", "x", "other room", now));

        using var store = new SqliteHistoryStore(Path.Combine(_dir, "history.db"));
        Assert.Equal(6, HistoryImport.FromFiles(store, _dir, cipher));

        Assert.Equal(good.Select(m => m.Text), store.Newest("roomA", 100).OrderBy(m => m.Ts).Select(m => m.Text));
        Assert.Equal("other room", Assert.Single(store.Newest("roomB", 100)).Text);
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.jsonl"));

        // Doing it again finds nothing more to do.
        Assert.Equal(0, HistoryImport.FromFiles(store, _dir, cipher));
    }
}
