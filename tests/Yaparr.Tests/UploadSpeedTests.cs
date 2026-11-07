using System.Diagnostics;
using Yaparr.Chat;
using Yaparr.Discovery;

namespace Yaparr.Tests;

// How much a member can send decides how many others it passes a stream on to.
[Collection("real-time audio")]
public class UploadSpeedTests
{
    [Fact]
    public void Nothing_changes_until_the_upload_has_been_measured_and_then_a_slow_line_serves_fewer_members()
    {
        // Not measured: the configured numbers.
        Assert.Equal(384 * 1024, UploadPlan.ForwardBytesPerSecond(null, 384 * 1024));
        Assert.Equal(3, UploadPlan.Fanout(null, 1000, 3));

        // 2 Mbit/s up (250 KB/s): a screen at 1 Mbit/s fits once; voice, at 64 kbit/s, many times (capped).
        Assert.Equal(128 * 1024, UploadPlan.ForwardBytesPerSecond(250 * 1024, 384 * 1024));
        Assert.Equal(1, UploadPlan.Fanout(250 * 1024, 1000, 3));
        Assert.Equal(UploadPlan.MaxFanout, UploadPlan.Fanout(250 * 1024, 64, 3));

        // 40 Mbit/s up (5 MB/s): two fifths of it, up to a limit, so a screen can go to several members.
        Assert.Equal(UploadPlan.MaxForward, UploadPlan.ForwardBytesPerSecond(5 * 1024 * 1024, 384 * 1024));
        Assert.Equal(UploadPlan.MaxFanout, UploadPlan.Fanout(5 * 1024 * 1024, 1000, 3));

        // 8 Mbit/s up (1 MB/s) serves a screen to three.
        Assert.Equal(3, UploadPlan.Fanout(1024 * 1024, 1000, 3));
    }

    [Fact]
    public void The_sharer_sends_to_as_many_members_as_its_own_upload_carries()
    {
        // Not measured: the places it was started with.
        Assert.Equal(3, UploadPlan.RootFanout(null, 4000, 3));
        // 10 Mbit/s up (1.25 MB/s): two fifths is 0.5 MB/s, one copy of a 4 Mbit/s screen (0.5 MB/s), not three.
        Assert.Equal(1, UploadPlan.RootFanout(1_250_000, 4000, 3));
        // The same line carries a 1 Mbit/s screen to three.
        Assert.Equal(3, UploadPlan.RootFanout(1_250_000, 1000, 3));
        // A very fast line (25 MB/s) sends 1080p at 6 Mbit/s to three, which the 2 MB/s limit for members passing a stream on would not allow.
        Assert.Equal(3, UploadPlan.RootFanout(25_000_000, 6000, 3));
        Assert.True(UploadPlan.Fanout(5 * 1024 * 1024, 6000, 3) < 3);
        // Never more than it was started with, and the dialog asks the same question.
        Assert.Equal(2, UploadPlan.RootFanout(25_000_000, 6000, 2));
        Assert.Equal(6, ChatNode.DirectViewersAt(25_000_000, 4000));   // a fast line uses more places than the default three
        Assert.Equal(3, UploadPlan.RootFanout(25_000_000, 4000, 3));      // unless the number was chosen by the caller
        Assert.Equal(6, UploadPlan.RootFanout(25_000_000, 4000, 3, mayGrow: true));
        Assert.Equal(1, UploadPlan.RootFanout(1_250_000, 4000, 3, mayGrow: true));
        Assert.Equal(1, ChatNode.DirectViewersAt(1_250_000, 4000));
    }

    [Fact]
    public async Task A_burst_of_packets_to_a_peer_measures_the_speed_of_the_narrowest_link_and_a_second_test_is_refused()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = TestUtil.NewNode(hub, "alice");
        using var b = TestUtil.NewNode(hub, "bob");
        var ra = await a.JoinRoomAsync("speed");
        var rb = await b.JoinRoomAsync("speed");
        await TestUtil.WaitFor(() => ra.MemberStates.Count == 1 && rb.MemberStates.Count == 1, 30_000);

        // Alice's line carries 400 KB/s (about 3 Mbit/s): her packets leave no faster than that.
        const double line = 400 * 1024;
        var clock = Stopwatch.StartNew();
        double startedAt = -1; long sentBytes = 0;
        a.Mux.OutboundFilter = (packet, _) =>
        {
            if (packet.Length < 1000) return true;
            // Each packet leaves when the bytes before it have had time to go, however late the one before was.
            if (startedAt < 0) startedAt = clock.Elapsed.TotalSeconds;
            double due = startedAt + sentBytes / line;
            if (clock.Elapsed.TotalSeconds < due) SpinWait.SpinUntil(() => clock.Elapsed.TotalSeconds >= due);
            sentBytes += packet.Length;
            return true;
        };

        var rate = await a.MeasureUploadAsync(b.Id, CancellationToken.None);

        Assert.NotNull(rate);
        Assert.InRange(rate!.Value, (long)(line * 0.6), (long)(line * 1.3));

        // Bob does not take part in another test so soon after.
        Assert.Null(await a.MeasureUploadAsync(b.Id, CancellationToken.None));
    }
}
