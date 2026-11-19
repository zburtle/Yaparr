using Yaparr.Chat;
using Yaparr.Transport;
using Yaparr.Vpn;

namespace Yaparr.AppCore;

/// <summary>
/// When the network does not let UDP out (<see cref="ChatNode.UdpBlocked"/>: not one STUN server answered) and this device is signed in to a Yaparr Cloud server, the node's UDP traffic
/// goes through the server's gateway (<see cref="GatewayTunnel"/>), so that messages work. It stays until the network changes (the node lets the gateway go then, and the check of the new
/// network asks again). If the gateway does not work (the server has it off, the sign-in is no longer good, nothing comes back through it) it is let go, and not tried again for ten
/// minutes, so a network that is simply offline is not tried over and over.
/// </summary>
public sealed class GatewayFallback : IDisposable
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

    private readonly ChatNode _node;
    private readonly Func<(Uri Server, string Token)?> _account;
    private readonly Func<bool> _enabled;
    private readonly Action<string>? _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<Uri, string, IGatewayLink> _links;
    private int _busy;
    private DateTimeOffset _retryAfter;

    /// <param name="account">The Yaparr Cloud server and sign-in of this device, or null when it is not signed in.</param>
    /// <param name="enabled">Whether the person allows this (a setting).</param>
    public GatewayFallback(ChatNode node, Func<(Uri Server, string Token)?> account, Func<bool> enabled, Action<string>? log = null, Func<DateTimeOffset>? now = null, Func<Uri, string, IGatewayLink>? links = null)
    {
        _links = links ?? ((server, token) => new WebSocketGatewayLink(server, token));
        _node = node;
        _account = account;
        _enabled = enabled;
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        node.UdpBlocked += OnBlocked;
    }

    public void Dispose() => _node.UdpBlocked -= OnBlocked;

    /// <summary>Whether an attempt is under way.</summary>
    internal bool Busy => Volatile.Read(ref _busy) == 1;

    /// <summary>The server and sign-in from what the app keeps about Yaparr Cloud, or null when the device is signed out.</summary>
    public static (Uri Server, string Token)? AccountOf(BackupServerState state) =>
        state.SignedIn && Uri.TryCreate(state.Url, UriKind.Absolute, out var uri) ? (uri, state.Token) : null;

    // Raised while the node is looking at the network, with the node's gate held: what follows has to look at the network again, so it is not done here.
    private void OnBlocked() => _ = Task.Run(TryAsync);

    internal async Task TryAsync()
    {
        if (!_enabled() || _account() is not { } account) return;
        if (_now() < _retryAfter || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            _log?.Invoke("UDP does not get out of this network; trying Yaparr Cloud for messages");
            bool connected = await _node.ConnectGatewayAsync(() => _links(account.Server, account.Token), "Yaparr Cloud").ConfigureAwait(false);
            // Connected, and the check of the network through it found an address: it works. Anything else is let go.
            if (connected && _node.NetworkStatus.Nat.PublicEndPoint != null) return;
            _log?.Invoke(connected ? "nothing comes back through the Yaparr Cloud gateway; letting it go" : "the Yaparr Cloud gateway could not be reached or is off; letting it go");
            await _node.DisconnectGatewayAsync().ConfigureAwait(false);
            _retryAfter = _now() + Cooldown;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.Invoke("Yaparr Cloud gateway: " + ex.Message);
            _retryAfter = _now() + Cooldown;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }
}
