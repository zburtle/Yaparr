using Yaparr.Transport;

namespace Yaparr.Tests;

// Anyone can open a connection, so what a peer can make us hold while it sends a message is bounded.
public class ReliableLimitTests
{
    [Fact]
    public void A_message_that_never_ends_is_refused_once_it_passes_the_limit()
    {
        var channel = new ReliableChannel();
        var delivered = new List<byte[]>();
        var piece = new byte[60_000];
        bool ok = true;
        uint seq = 0;
        for (int i = 0; i < 100 && ok; i++) ok = channel.OnSegment(seq++, last: false, piece, delivered);

        Assert.False(ok);
        Assert.Empty(delivered);
    }

    [Fact]
    public void A_large_message_within_the_limit_is_delivered()
    {
        var channel = new ReliableChannel();
        var delivered = new List<byte[]>();
        var piece = new byte[1000];
        for (uint seq = 0; seq < 1000; seq++) Assert.True(channel.OnSegment(seq, last: seq == 999, piece, delivered));

        Assert.Single(delivered);
        Assert.Equal(1_000_000, delivered[0].Length);
    }

    [Fact]
    public void Segments_far_out_of_order_are_not_all_kept()
    {
        var channel = new ReliableChannel();
        var delivered = new List<byte[]>();
        var piece = new byte[60_000];
        for (uint seq = 1; seq < 600; seq++) channel.OnSegment(seq, last: false, piece, delivered);

        // Sending the missing first piece now must not unleash more than the limit.
        Assert.True(channel.OnSegment(0, last: true, new byte[10], delivered));
        Assert.True(delivered.Count == 1 && delivered[0].Length < 2 * 1024 * 1024);
    }
}
