using Yaparr.Chat;

namespace Yaparr.Tests;

public class ResendTests
{
    private static byte[] Packet(uint seq) => new byte[] { 1, (byte)seq };

    [Fact]
    public void Packets_are_kept_for_resending_by_age_not_by_a_count_of_six_hundred()
    {
        var g = new MediaGroup();
        // 1500 packets in one second (about 12 Mbit/s): the old store held the last 600 and so lost most of a second's worth.
        for (uint seq = 1; seq <= 1500; seq++) ChatNode.KeepRecent(g, seq, Packet(seq), now: 10_000 + seq / 2);
        Assert.Equal(1500, g.Recent.Count);
        Assert.True(g.Recent.ContainsKey(1), "a packet from the start of the second is still there");

        // Two seconds later the oldest are let go, in order.
        ChatNode.KeepRecent(g, 1501, Packet(1501), now: 10_000 + ChatNode.RecentKeepMs + 100);
        Assert.False(g.Recent.ContainsKey(1));
        Assert.True(g.Recent.ContainsKey(1501));
        Assert.True(g.Recent.Count < 1500);
    }

    [Fact]
    public void However_fast_the_stream_the_store_stays_within_its_limit()
    {
        var g = new MediaGroup();
        for (uint seq = 1; seq <= 20_000; seq++) ChatNode.KeepRecent(g, seq, Packet(seq), now: 5_000);
        Assert.Equal(ChatNode.RecentMaxPackets, g.Recent.Count);
        Assert.True(g.Recent.ContainsKey(20_000));
        Assert.False(g.Recent.ContainsKey(1));
    }

    [Fact]
    public void A_packet_that_does_not_come_is_asked_for_four_times_each_a_little_later_than_the_last()
    {
        var g = new MediaGroup();
        long start = 100_000;
        g.Missing[7] = start;
        var askedAt = new List<long>();
        for (long now = start; now <= start + 1500; now += 10)
            if (ChatNode.SelectAsks(g, now).Contains(7u)) askedAt.Add(now - start);
        Assert.Equal(ChatNode.MaxAsks, askedAt.Count);
        Assert.True(askedAt[0] is >= 30 and <= 40, $"first ask after {askedAt[0]} ms");
        // 80, 160 and 320 ms between the asks: the answer has time to come, and a link that is losing is not asked more and more often.
        var gaps = askedAt.Zip(askedAt.Skip(1), (a, b) => b - a).ToList();
        Assert.True(gaps[0] >= 80 && gaps[1] >= 160 && gaps[2] >= 320, string.Join(", ", gaps));
        Assert.True(askedAt[^1] < 700, $"last ask after {askedAt[^1]} ms, when the frame has long been given up on");
    }

    [Fact]
    public void Many_missing_packets_are_asked_for_in_batches_of_at_most_thirty_two()
    {
        var g = new MediaGroup();
        for (uint s = 1; s <= 100; s++) g.Missing[s] = 1000;
        Assert.Equal(32, ChatNode.SelectAsks(g, 1100).Count);
        Assert.Equal(32, ChatNode.SelectAsks(g, 1100).Count);   // the next thirty-two, not the same ones again
    }

    private static byte[] Frame(int n, int size) => Enumerable.Range(0, size).Select(i => (byte)(n * 31 + i)).ToArray();
}
