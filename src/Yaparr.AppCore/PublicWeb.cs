using System.Net;
using System.Net.Sockets;

namespace Yaparr.AppCore;

/// <summary>
/// Reaching a web address on behalf of the person: no proxy and no cookies, and the connection is made here so that an address
/// inside a home or office network is refused (a link in a message must not make the app knock on the router or on a machine next door).
/// A redirect goes through the same check.
/// </summary>
internal static class PublicWeb
{
    public static SocketsHttpHandler CreateHandler(int maxRedirects = 3) => new()
    {
        UseProxy = false,
        UseCookies = false,
        // A site may compress a page whether or not it was asked to; what is read from it is capped after it is unpacked.
        AutomaticDecompression = DecompressionMethods.All,
        MaxAutomaticRedirections = maxRedirects,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
            var allowed = addresses.Where(IsPublic).ToArray();
            if (allowed.Length == 0) throw new HttpRequestException("That address is not on the public internet.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        },
    };

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6Multicast && (address.GetAddressBytes()[0] & 0xFE) != 0xFC;
        var b = address.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 127 || b[0] == 0 || b[0] >= 224
                 || (b[0] == 172 && b[1] is >= 16 and <= 31)
                 || (b[0] == 192 && b[1] == 168)
                 || (b[0] == 169 && b[1] == 254)
                 || (b[0] == 100 && b[1] is >= 64 and <= 127));
    }
}
