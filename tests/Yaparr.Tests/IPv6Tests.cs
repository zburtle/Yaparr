using System.Net;
using System.Net.Sockets;
using Yaparr.Discovery.Dht;
using Yaparr.Identity;
using Yaparr.Transport;

namespace Yaparr.Tests;

public class IPv6PeerRecordTests
{
    private static IPEndPoint Ep(string address, int port) => new(IPAddress.Parse(address), port);

    [Fact]
    public void A_record_carries_IPv4_and_IPv6_endpoints_and_both_are_read_back()
    {
        using var identity = PeerIdentity.Generate();
        var value = PeerRecord.Encode(identity, new[] { Ep("203.0.113.5", 4000), Ep("2606:4700:4700::1111", 5000) }, 1_700_000_000);
        var decoded = PeerRecord.Decode(identity.Id, value);

        Assert.NotNull(decoded);
        Assert.Contains(Ep("203.0.113.5", 4000), decoded!.EndPoints);
        Assert.Contains(Ep("2606:4700:4700::1111", 5000), decoded.EndPoints);
        Assert.Equal(2, decoded.EndPoints.Count);
    }

    [Fact]
    public void An_IPv4_mapped_address_is_stored_as_plain_IPv4()
    {
        using var identity = PeerIdentity.Generate();
        var value = PeerRecord.Encode(identity, new[] { Ep("::ffff:203.0.113.5", 4000) }, 1);
        var decoded = PeerRecord.Decode(identity.Id, value);

        Assert.Equal(Ep("203.0.113.5", 4000), Assert.Single(decoded!.EndPoints));
    }

    [Fact]
    public void A_tampered_IPv6_part_is_ignored_and_the_IPv4_part_still_counts()
    {
        using var identity = PeerIdentity.Generate();
        var value = PeerRecord.Encode(identity, new[] { Ep("203.0.113.5", 4000), Ep("2606:4700:4700::1111", 5000) }, 1);
        // Change the IPv6 port inside the signed entries so its signature no longer matches.
        var text = System.Text.Encoding.Latin1.GetString(value);
        var idx = text.IndexOf("2:e6", StringComparison.Ordinal);
        Assert.True(idx >= 0);
        var bytes = value.ToArray();
        int entry = text.IndexOf("18:", idx, StringComparison.Ordinal) + 3;
        bytes[entry + 17] ^= 0x01;

        var decoded = PeerRecord.Decode(identity.Id, bytes);
        Assert.NotNull(decoded);
        Assert.Equal(Ep("203.0.113.5", 4000), Assert.Single(decoded!.EndPoints));
    }

    [Fact]
    public void Link_local_and_unique_local_addresses_are_not_published()
    {
        using var identity = PeerIdentity.Generate();
        var value = PeerRecord.Encode(identity, new[] { Ep("fe80::1", 4000), Ep("fd00::1", 4000) }, 1);
        Assert.Empty(PeerRecord.Decode(identity.Id, value)!.EndPoints);
    }

    [Fact]
    public void Reachable_IPv6_excludes_local_only_ranges()
    {
        Assert.True(PeerRecord.IsReachableIPv6(IPAddress.Parse("2606:4700:4700::1111")));
        Assert.False(PeerRecord.IsReachableIPv6(IPAddress.Parse("::1")));
        Assert.False(PeerRecord.IsReachableIPv6(IPAddress.Parse("fe80::1")));
        Assert.False(PeerRecord.IsReachableIPv6(IPAddress.Parse("fd12::1")));
        Assert.False(PeerRecord.IsReachableIPv6(IPAddress.Parse("ff02::1")));
        Assert.False(PeerRecord.IsReachableIPv6(IPAddress.Parse("203.0.113.5")));
    }
}

public class IPv6MuxTests
{
    [Fact]
    public async Task The_mux_sends_and_receives_IPv6_and_reports_IPv4_peers_in_plain_form()
    {
        if (!Socket.OSSupportsIPv6) return; // nothing to check on a system without IPv6

        using var mux = new UdpMux();
        Assert.True(mux.DualStack);
        var got = new TaskCompletionSource<(byte[] Data, IPEndPoint From)>();
        mux.AddHandler((p, from) => { got.TrySetResult((p.ToArray(), from)); return true; });
        mux.Start();

        using var peer = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        var peerEndPoint = (IPEndPoint)peer.Client.LocalEndPoint!;

        // Outgoing: the mux sends to an IPv6 peer.
        mux.Send(new byte[] { 4, 2 }, peerEndPoint);
        var fromMux = await peer.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new byte[] { 4, 2 }, fromMux.Buffer);

        // Incoming: the peer sends from IPv6 and the handler sees that address.
        await peer.SendAsync(new byte[] { 9 }, 1, new IPEndPoint(IPAddress.IPv6Loopback, mux.LocalPort));
        var (data, from) = await got.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new byte[] { 9 }, data);
        Assert.Equal(AddressFamily.InterNetworkV6, from.AddressFamily);
        Assert.Equal(peerEndPoint.Port, from.Port);
    }

    [Fact]
    public void A_mapped_IPv4_address_is_reported_as_plain_IPv4()
    {
        var mapped = new IPEndPoint(IPAddress.Parse("::ffff:192.0.2.7"), 1234);
        var plain = UdpMux.AsCanonical(mapped);
        Assert.Equal(AddressFamily.InterNetwork, plain.AddressFamily);
        Assert.Equal(IPAddress.Parse("192.0.2.7"), plain.Address);
        Assert.Equal(1234, plain.Port);
    }
}
