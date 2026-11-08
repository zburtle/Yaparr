using System.Net;
using Yaparr.Chat;
using Yaparr.Transport;

namespace Yaparr.Tests;

public class NetworkCheckExplanationTests
{
    private static readonly IPEndPoint Address = new(IPAddress.Parse("203.0.113.7"), 40000);

    [Fact]
    public void Cannot_reach_the_internet_is_said_only_when_no_stun_server_answered_at_all()
    {
        var none = NetworkCheck_NoReplies(NatReport.Unknown, vpnUp: false, error: null);
        Assert.NotNull(none);
        Assert.Equal(CheckLevel.Problem, none!.Level);
        Assert.Equal("Cannot reach the internet", none.Title);
    }

    [Fact]
    public void One_answer_is_enough_to_know_the_address_so_it_is_not_no_internet()
    {
        // Only one of the servers answered: the type cannot be told (Unknown) but the public address is known.
        var oneAnswer = new NatReport(NatType.Unknown, Address, new[] { Address });
        Assert.Null(NetworkCheck_NoReplies(oneAnswer, vpnUp: false, error: null));
        Assert.Equal(NatType.Unknown, NatProbe.Classify(new[] { Address }, Array.Empty<IPAddress>()).Type);
        Assert.NotNull(NatProbe.Classify(new[] { Address }, Array.Empty<IPAddress>()).PublicEndPoint);
    }

    [Fact]
    public void When_looking_at_the_network_itself_failed_the_check_says_so_and_not_that_the_internet_is_down()
    {
        var failed = NetworkCheck_NoReplies(NatReport.Unknown, vpnUp: false, error: "The information requested is unavailable on the current platform.");
        Assert.NotNull(failed);
        Assert.Equal(CheckLevel.Warning, failed!.Level);
        Assert.Equal("Could not look at the network", failed.Title);
        Assert.Contains("unavailable on the current platform", failed.Detail);
        Assert.Contains("does not mean the internet cannot be reached", failed.Detail);
    }

    [Fact]
    public void Nothing_is_said_when_the_type_is_known_or_a_vpn_carries_the_traffic()
    {
        Assert.Null(NetworkCheck_NoReplies(new NatReport(NatType.Open, Address, new[] { Address }), vpnUp: false, error: null));
        Assert.Null(NetworkCheck_NoReplies(NatReport.Unknown, vpnUp: true, error: null));
    }

    [Fact]
    public void Looking_for_the_gateway_and_the_addresses_does_not_throw_whatever_the_platform_says()
    {
        // Android answers some of these questions with PlatformNotSupportedException; here they just have to come back.
        _ = PortMapper.FindGateway();
        var report = NatProbe.Classify(Array.Empty<IPEndPoint>(), Array.Empty<IPAddress>());
        Assert.Equal(NatType.Unknown, report.Type);
    }

    private static CheckItem? NetworkCheck_NoReplies(NatReport nat, bool vpnUp, string? error) => ChatNode.NoRepliesItem(nat, vpnUp, error);
}
