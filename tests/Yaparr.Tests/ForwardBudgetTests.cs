using Yaparr.Chat;
using Yaparr.Voice;

namespace Yaparr.Tests;

// What a node that passes voice on does when it is short of room: frames their senders call silence go first.
public class ForwardBudgetTests
{
    private static byte[] Packet(uint timestamp, byte level = 0)
    {
        var packet = new byte[VoicePacket.HeaderSize + 20];
        VoicePacket.Write(packet, 7, 1, timestamp, false, new byte[20], level);
        return packet;
    }

    [Fact]
    public void Only_frames_their_sender_calls_silence_are_taken_for_silence()
    {
        Assert.True(VoicePacket.SaysSilence(Packet(960 * 40, VoicePacket.EncodeLevel(-62f, false))));
        Assert.False(VoicePacket.SaysSilence(Packet(960 * 40, VoicePacket.EncodeLevel(-20f, true))));
        // An older version says nothing about its audio; that is not silence.
        Assert.False(VoicePacket.SaysSilence(Packet(960 * 40)));
        Assert.False(VoicePacket.SaysSilence(new byte[] { 1, 2, 3 }));
        Assert.False(VoicePacket.SaysSilence(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void The_forwarding_budget_holds_a_second_and_refills_at_its_rate()
    {
        // As ChatNode makes it: a second's worth, refilled at that rate.
        var bucket = new TokenBucket(1000, 1000);
        long t = Environment.TickCount64;
        Assert.True(bucket.TryTake(600, t));
        Assert.False(bucket.TryTake(600, t));       // only 400 left, and nothing has refilled yet
        Assert.False(bucket.TryTake(600, t + 100)); // 500 now
        Assert.True(bucket.TryTake(600, t + 300));  // 700 now
        Assert.False(bucket.TryTake(1000, t + 300)); // not the whole second's worth again straight away
    }

    [Fact]
    public void Something_that_may_only_use_what_is_spare_leaves_the_rest_for_the_others()
    {
        var bucket = new TokenBucket(1000, 1000);
        long t = Environment.TickCount64;
        int silence = 0;
        while (bucket.TryTake(100, t, keep: bucket.Capacity / 2)) silence++;
        Assert.Equal(5, silence);                   // 1000 down to 500, and no further
        int speech = 0;
        while (bucket.TryTake(100, t)) speech++;
        Assert.Equal(5, speech);                    // the half that was kept is still there for speech
    }
}
