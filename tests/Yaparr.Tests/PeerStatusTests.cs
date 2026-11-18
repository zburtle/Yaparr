using Yaparr.AppCore;
using Yaparr.Chat;

namespace Yaparr.Tests;

public sealed class PeerStatusTests
{
    private static readonly DateTimeOffset Seen = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(PeerLink.Direct, "direct")]
    [InlineData(PeerLink.Relayed, "via member")]
    [InlineData(PeerLink.ViaServer, "via relay server")]
    [InlineData(PeerLink.Reconnecting, "reconnecting…")]
    [InlineData(PeerLink.NotResponding, "not responding")]
    public void Each_way_of_being_connected_has_a_short_status_and_a_tip_that_says_what_it_means(PeerLink link, string status)
    {
        Assert.Equal(status, PeerStatus.Text(link, true, false, usesCloud: true));
        var tip = PeerStatus.Tip(link, true, false, true, true, Seen);
        Assert.NotEmpty(tip);
        Assert.True(tip.Length < 120, tip);
    }

    [Fact]
    public void Someone_who_is_away_says_so_and_says_when_they_use_Yaparr_Cloud()
    {
        Assert.Equal("offline", PeerStatus.Text(PeerLink.Offline, false, false, usesCloud: false));
        Assert.Equal("offline · on Yaparr Cloud", PeerStatus.Text(PeerLink.Offline, false, false, usesCloud: true));
        Assert.StartsWith("Not connected. Last seen", PeerStatus.Tip(PeerLink.Offline, false, false, false, true, Seen));
        Assert.Contains("waits for them on the server", PeerStatus.Tip(PeerLink.Offline, false, false, true, iUseCloud: true, Seen));
        // Messages only wait for them if you are signed in too, and the tip says so.
        Assert.Contains("sign in to Yaparr Cloud too", PeerStatus.Tip(PeerLink.Offline, false, false, true, iUseCloud: false, Seen));
    }

    [Fact]
    public void Someone_who_is_here_but_reached_through_a_member_and_one_who_is_just_online_read_as_before()
    {
        Assert.Equal("via member", PeerStatus.Text(PeerLink.Offline, true, true, false));
        Assert.Equal("online", PeerStatus.Text(PeerLink.Offline, true, false, false));
        Assert.Equal("Online.", PeerStatus.Tip(PeerLink.Offline, true, false, false, false, Seen));
    }
}
