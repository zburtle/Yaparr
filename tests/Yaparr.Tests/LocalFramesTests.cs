using Yaparr.Chat;
using Yaparr.Screen;

namespace Yaparr.Tests;

public class LocalFramesTests
{
    [Fact]
    public void Frames_pushed_by_hand_reach_a_viewer_in_order_with_their_key_flag()
    {
        var frames = new LocalFrames();
        var seen = new List<(byte[] Data, bool IsKey, uint Id)>();
        IMediaFrameSource source = frames;
        source.FrameReceived += f => seen.Add((f.Data.ToArray(), f.IsKey, f.Id));

        frames.Push(new byte[] { 1, 2 }, true);
        frames.Push(new byte[] { 3 }, false);

        Assert.Equal(2, seen.Count);
        Assert.Equal(new byte[] { 1, 2 }, seen[0].Data);
        Assert.True(seen[0].IsKey);
        Assert.False(seen[1].IsKey);
        Assert.True(seen[1].Id > seen[0].Id);
    }

    [Fact]
    public void Nothing_happens_when_nobody_is_listening()
    {
        new LocalFrames().Push(new byte[] { 1 }, true);
    }
}
