using System.Net;

namespace Yaparr.Vpn;

public sealed class WireGuardConfig
{
    public required byte[] PrivateKey { get; init; }
    public required IPAddress Address { get; init; }
    public required byte[] PeerPublicKey { get; init; }
    public byte[]? PresharedKey { get; init; }
    public required string Endpoint { get; init; }
    public int Mtu { get; init; } = 1420;
    public int PersistentKeepalive { get; init; }

    public string EndpointHost => SplitEndpoint(Endpoint).Host;

    public static (string Host, int Port) SplitEndpoint(string endpoint)
    {
        endpoint = endpoint.Trim();
        int i = endpoint.LastIndexOf(':');
        if (i <= 0 || !int.TryParse(endpoint[(i + 1)..], out var port) || port is < 1 or > 65535)
            throw new FormatException($"The endpoint '{endpoint}' must look like host:port.");
        return (endpoint[..i].Trim('[', ']'), port);
    }

    public static WireGuardConfig Load(string path) => Parse(File.ReadAllText(path));

    public static WireGuardConfig Parse(string text)
    {
        var iface = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var peers = new List<Dictionary<string, string>>();
        Dictionary<string, string>? current = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash].Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('['))
            {
                if (line.Equals("[Interface]", StringComparison.OrdinalIgnoreCase)) current = iface;
                else if (line.Equals("[Peer]", StringComparison.OrdinalIgnoreCase)) peers.Add(current = new(StringComparer.OrdinalIgnoreCase));
                else throw new FormatException($"Unknown section {line}.");
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0 || current == null) throw new FormatException($"Cannot read the line '{Truncate(line)}'.");
            current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }

        if (!iface.TryGetValue("PrivateKey", out var privateKey)) throw new FormatException("The [Interface] section has no PrivateKey.");
        if (!iface.TryGetValue("Address", out var addresses)) throw new FormatException("The [Interface] section has no Address.");
        if (peers.Count == 0) throw new FormatException("There is no [Peer] section.");
        var peer = peers[0];
        if (!peer.TryGetValue("PublicKey", out var publicKey)) throw new FormatException("The [Peer] section has no PublicKey.");
        if (!peer.TryGetValue("Endpoint", out var endpoint)) throw new FormatException("The [Peer] section has no Endpoint.");
        SplitEndpoint(endpoint);

        var address = addresses.Split(',')
            .Select(a => a.Trim().Split('/')[0])
            .Select(a => IPAddress.TryParse(a, out var ip) ? ip : null)
            .FirstOrDefault(ip => ip is { AddressFamily: System.Net.Sockets.AddressFamily.InterNetwork })
            ?? throw new FormatException("The interface needs an IPv4 Address.");

        int mtu = iface.TryGetValue("MTU", out var m) && int.TryParse(m, out var parsedMtu) && parsedMtu is >= 576 and <= 1500 ? parsedMtu : 1420;
        int keepalive = peer.TryGetValue("PersistentKeepalive", out var k) && int.TryParse(k, out var parsedKeepalive) && parsedKeepalive > 0 ? parsedKeepalive : 0;

        return new WireGuardConfig
        {
            PrivateKey = DecodeKey(privateKey, "PrivateKey"),
            Address = address,
            PeerPublicKey = DecodeKey(publicKey, "PublicKey"),
            PresharedKey = peer.TryGetValue("PresharedKey", out var psk) ? DecodeKey(psk, "PresharedKey") : null,
            Endpoint = endpoint,
            Mtu = mtu,
            PersistentKeepalive = keepalive,
        };
    }

    private static byte[] DecodeKey(string value, string name)
    {
        try
        {
            var key = Convert.FromBase64String(value);
            if (key.Length == WireGuardCrypto.KeyLength) return key;
        }
        catch (FormatException) { }
        throw new FormatException($"{name} is not a valid WireGuard key.");
    }

    private static string Truncate(string line) => line.Length <= 24 ? line : line[..24] + "…";

    public string Describe() => $"{Endpoint} as {Address}";
}
