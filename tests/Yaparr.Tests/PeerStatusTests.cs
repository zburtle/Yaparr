using Yaparr.AppCore;
using Yaparr.Chat;

namespace Yaparr.Tests;

public sealed class PeerStatusTests
{
    private static readonly DateTimeOffset Seen = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(PeerLink.Direct, "direct", "Connected directly")]
    [InlineData(PeerLink.Relayed, "via member", "Through another member")]
    [InlineData(PeerLink.ViaServer, "via relay server", "Through the relay server")]
    [InlineData(PeerLink.Reconnecting, "reconnecting…", "Reconnecting")]
    [InlineData(PeerLink.NotResponding, "not responding", "Not responding")]
    public void Each_way_of_being_connected_has_a_short_status_a_title_and_a_short_detail(PeerLink link, string status, string title)
    {
        Assert.Equal(status, PeerStatus.Text(link, true, false, usesCloud: true));
        Assert.Equal(title, PeerStatus.Title(link, true, false, true));
        var detail = PeerStatus.Detail(link, true, false, true, true);
        Assert.NotEmpty(detail);
        Assert.True(detail.Length < 80, detail);
        Assert.False(PeerStatus.HasSeen(link, true));
    }

    [Fact]
    public void Someone_who_is_away_says_so_says_when_and_says_when_they_use_Yaparr_Cloud()
    {
        Assert.Equal("offline", PeerStatus.Text(PeerLink.Offline, false, false, usesCloud: false));
        Assert.Equal("offline · on Yaparr Cloud", PeerStatus.Text(PeerLink.Offline, false, false, usesCloud: true));
        Assert.Equal("Offline", PeerStatus.Title(PeerLink.Offline, false, false, false));
        Assert.Equal("Offline, on Yaparr Cloud", PeerStatus.Title(PeerLink.Offline, false, false, true));
        Assert.True(PeerStatus.HasSeen(PeerLink.Offline, false));
        Assert.StartsWith("Last seen ", PeerStatus.Seen(Seen));
        Assert.Contains("waits for them on the server for up to 7 days", PeerStatus.Detail(PeerLink.Offline, false, false, true, iUseCloud: true));
        // Messages only wait for them if you are signed in too, and the detail says so.
        Assert.Contains("Sign in to Yaparr Cloud too", PeerStatus.Detail(PeerLink.Offline, false, false, true, iUseCloud: false));
        Assert.DoesNotContain("Yaparr Cloud", PeerStatus.Detail(PeerLink.Offline, false, false, false, true));
    }

    [Fact]
    public void Someone_who_is_here_but_reached_through_a_member_and_one_who_is_just_online_read_as_before()
    {
        Assert.Equal("via member", PeerStatus.Text(PeerLink.Offline, true, true, false));
        Assert.Equal("online", PeerStatus.Text(PeerLink.Offline, true, false, false));
        Assert.Equal("Online", PeerStatus.Title(PeerLink.Offline, true, false, false));
        Assert.Equal("Here now.", PeerStatus.Detail(PeerLink.Offline, true, false, false, false));
    }

    [Fact]
    public void An_id_is_shown_in_two_lines_of_four_groups_of_eight()
    {
        var id = "788bade73ec1dc3132475ddc99729d4fb97c321e156bc2b23d8c9776f5741243";
        Assert.Equal("788bade7 3ec1dc31 32475ddc 99729d4f\nb97c321e 156bc2b2 3d8c9776 f5741243", PeerStatus.IdLines(id));
        Assert.Equal("short", PeerStatus.IdLines("short"));
    }
}
