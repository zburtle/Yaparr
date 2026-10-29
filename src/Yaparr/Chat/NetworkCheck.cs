using Yaparr.Identity;
using Yaparr.Transport;
using Yaparr.Vpn;

namespace Yaparr.Chat;

public enum CheckLevel { Ok, Info, Warning, Problem }

public sealed record CheckItem(CheckLevel Level, string Title, string Detail);

public sealed record NetworkCheckReport(IReadOnlyList<CheckItem> Items)
{
    public CheckLevel Overall => Items.Count == 0 ? CheckLevel.Info : Items.Max(i => i.Level);

    public string Summary => Overall switch
    {
        CheckLevel.Problem => "There is a problem that will stop some people from connecting.",
        CheckLevel.Warning => "Mostly working, but some connections may fail.",
        _ => "Everything looks fine.",
    };
}

public enum PeerLink
{
    /// <summary>Not connected and not being retried.</summary>
    Offline,
    /// <summary>Connected straight to the other person.</summary>
    Direct,
    /// <summary>Connected through another member who forwards traffic.</summary>
    Relayed,
    /// <summary>The connection dropped and Yaparr is trying again.</summary>
    Reconnecting,
    /// <summary>Connected, but the last message was not confirmed.</summary>
    NotResponding,
    /// <summary>Connected straight to the shared TURN server's address for them, which carries everything and has limited bandwidth.</summary>
    ViaServer,
}

public sealed partial class ChatNode
{
    private long _lastFoundAt;

    /// <summary>How Yaparr currently reaches one person.</summary>
    public PeerLink GetLink(PeerId peer)
    {
        // The id can be a device or a person; a person is reached through whichever of their devices is best connected.
        var state = _peers.TryGetValue(peer, out var exact) ? exact : DevicesOf(peer).OrderBy(s => s.Connection.IsRelayed).FirstOrDefault();
        if (state != null)
        {
            var device = state.Id;
            if (_outbox.Values.Any(o => o.Last is { State: DeliveryState.NotDelivered or DeliveryState.PartlyDelivered }
                                       && (o.DirectPeer == peer || o.DirectPeer == device || (o.Room != null && o.Targets.Contains(device) && !o.Acked.Contains(device)))))
                return PeerLink.NotResponding;
            if (state.Connection.IsRelayed) return PeerLink.Relayed;
            return IsOnSharedServer(state.Connection) ? PeerLink.ViaServer : PeerLink.Direct;
        }
        return _redial.ContainsKey(peer) ? PeerLink.Reconnecting : PeerLink.Offline;
    }

    /// <summary>
    /// Looks at the network again and explains in plain words what works and what does not.
    /// </summary>
    public async Task<NetworkCheckReport> CheckNetworkAsync(CancellationToken ct = default)
    {
        Start();
        await RefreshNetworkAsync(ct).ConfigureAwait(false);
        var items = new List<CheckItem>();
        var status = NetworkStatus;
        var turn = _turnOptions;
        bool vpnUp = VpnState == VpnState.Connected;

        if (!TraversalEnabled)
        {
            items.Add(new(CheckLevel.Info, "Network checks are off", "Port mapping and NAT detection are disabled in network.json."));
        }
        else if (status.Nat.Type == NatType.Unknown && !vpnUp)
        {
            items.Add(new(CheckLevel.Problem, "Cannot reach the internet",
                "No reply from the STUN servers. Check that you are online and that your firewall allows Yaparr to use UDP."));
        }
        else
        {
            switch (status.Nat.Type)
            {
                case NatType.Open:
                case NatType.EndpointIndependent:
                    items.Add(new(CheckLevel.Ok, "Other people can reach you", status.Nat.Describe()));
                    break;
                case NatType.Symmetric when vpnUp || status.Relay != null || status.Mapping != null:
                    items.Add(new(CheckLevel.Ok, "Your router is strict, but a workaround is active",
                        vpnUp ? "The VPN gives peers a stable address." : status.Relay != null ? "A relay carries your traffic." : "A port mapping is open on your router."));
                    break;
                case NatType.Symmetric:
                    items.Add(new(CheckLevel.Warning, "Your router makes direct connections hard",
                        "It uses a different public address for every destination (symmetric NAT). People on other networks will usually not be able to connect. Turn on the VPN or a TURN relay from the Network menu."));
                    break;
                default:
                    items.Add(new(CheckLevel.Info, "Network type", status.Nat.Describe()));
                    break;
            }

            items.Add(status.Mapping != null
                ? new(CheckLevel.Ok, "Router port is open", $"{status.Mapping.Method} opened port {status.Mapping.ExternalEndPoint.Port}.")
                : vpnUp
                    ? new(CheckLevel.Info, "Router port", "Not used while the VPN is connected.")
                    : new(CheckLevel.Info, "Router port", "Your router did not open a port automatically (UPnP and NAT-PMP are off or unsupported)."));
        }

        if (vpnUp && _tunnel is { } tunnel && tunnel.SilentFor > TimeSpan.FromSeconds(45))
            items.Add(new(CheckLevel.Problem, "The VPN is connected but silent",
                $"Nothing has come back from the VPN server for {(int)tunnel.SilentFor.TotalSeconds} seconds. If another device uses the same WireGuard configuration, only one of them can work at a time. Give each device its own configuration from your VPN provider."));

        if (_discoveries.OfType<Yaparr.Discovery.Dht.DhtDiscovery>().FirstOrDefault() is { } dht && !_rooms.IsEmpty)
        {
            var (queried, answered) = dht.Node.LastLookup;
            if (queried == 0)
                items.Add(new(CheckLevel.Info, "Peer discovery (DHT)", "No lookup has finished yet."));
            else if (answered == 0)
                items.Add(new(CheckLevel.Problem, "Peer discovery cannot reach the network",
                    $"None of {queried} DHT nodes answered. A firewall may be blocking UDP, or replies are not coming back (a shared VPN configuration does this)."));
            else
                items.Add(new(CheckLevel.Ok, "Peer discovery works", $"{answered} of {queried} DHT nodes answered; {dht.Node.KnownNodes} known."));
        }

        if (VpnState == VpnState.Connecting)
            items.Add(new(CheckLevel.Warning, "VPN is connecting", "No reply from the VPN server yet. Check the server address and keys."));
        else if (vpnUp)
            items.Add(new(CheckLevel.Ok, "VPN connected", "Yaparr's traffic goes through the VPN. Other apps are not affected."));

        if (turn is { Enabled: true })
            items.Add(status.Relay != null
                ? new(CheckLevel.Ok, "TURN relay active", $"Relay at {status.Relay}.")
                : new(CheckLevel.Warning, "TURN relay is not working",
                    "Yaparr could not get a relay from the server. Check the address, username and password, and that the server is reachable."));

        if (_options.EnableRelaying && !_rooms.IsEmpty && TraversalEnabled)
        {
            var relayed = RelayStats;
            items.Add(IsRelayCapable
                ? new(CheckLevel.Ok, "You help others connect",
                    relayed.ForwardedFrames == 0
                        ? "Your connection is good enough to pass traffic between members of your rooms who cannot reach each other. Nothing needed it yet."
                        : $"You have passed {relayed.ForwardedBytes / 1024} KB between members of your rooms (limited to about 2 Mbit/s).")
                : new(CheckLevel.Info, "Others relay for you",
                    "Your router is too strict to help other people connect, so you do not relay for anyone."));
        }

        if (!_rooms.IsEmpty)
        {
            int connected = _peers.Count;
            bool foundRecently = _lastFoundAt != 0 && Environment.TickCount64 - _lastFoundAt < 120_000;
            if (connected > 0)
            {
                int relayed = _peers.Values.Count(p => p.Connection.IsRelayed);
                items.Add(new(CheckLevel.Ok, $"Connected to {connected} {(connected == 1 ? "person" : "people")}",
                    relayed > 0 ? $"{relayed} through other members." : "All directly."));
            }
            else if (foundRecently)
                items.Add(new(CheckLevel.Warning, "People were found but none connected",
                    "Their addresses were found, but connecting failed. A strict router or firewall on either side is the usual cause."));
            else
                items.Add(new(CheckLevel.Info, "Nobody else found yet",
                    "Discovery found no one in your rooms. Make sure the other person is online and has joined the same room or used the same invite."));
        }

        return new NetworkCheckReport(items);
    }
}
