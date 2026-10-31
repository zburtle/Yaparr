using Yaparr.Identity;

namespace Yaparr.Tests;

[CollectionDefinition("large room", DisableParallelization = true)]
public sealed class LargeRoomCollection { }

// Twenty nodes in one process need most of the machine. Run alongside other tests it misses its deadlines, so it runs
// by itself, after the parallel tests have finished.
[Collection("large room")]
public class LargeRoomTests
{
    [Fact]
    public async Task A_whole_room_of_twenty_can_watch_one_stream_when_nobody_sends_to_more_than_three()
    {
        using var c = await MediaTreeTests.Cluster.StartAsync(20, fanout: 3, rootFanout: 3);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "big screen");
        var views = await c.WatchAllAsync(source.Info.Id);
        try
        {
            var received = views.Select(MediaTreeTests.Collect).ToList();
            await MediaTreeTests.SendRange(source, 0, 40);
            try { await TestUtil.WaitFor(() => received.All(q => q.Count >= 36), 20_000); }
            catch (TimeoutException)
            {
                var report = string.Join("; ", views.Select((v, i) =>
                {
                    var st = v.Status;
                    return $"n{i + 1}: got {received[i].Count} parent={st.Parent?.Short ?? "-"} kids={st.Children.Count} dropped={st.Dropped} dup={st.Duplicates} rec={st.Recovered} resent={st.Retransmitted}";
                }));
                var root = source.Status;
                Assert.Fail($"root kids={root.Children.Count} dropped={root.Dropped} forwarded={root.Forwarded}; " + report);
            }

            var parentOf = views.Select((v, i) => (Id: c.Nodes[i + 1].Id, v.Status.Parent)).ToDictionary(x => x.Id, x => x.Parent!.Value);
            int Depth(PeerId id) { int d = 1; while (parentOf[id] != c.Root.Id) { id = parentOf[id]; d++; Assert.True(d < 20); } return d; }
            Assert.True(source.Status.Children.Count <= 3);
            Assert.True(parentOf.Keys.Max(Depth) >= 2, "nineteen viewers cannot all sit directly under a sharer who sends to three");
            Assert.All(views, v => Assert.True(v.Status.Children.Count <= 3));
        }
        finally { foreach (var v in views) v.Dispose(); }
    }
}
