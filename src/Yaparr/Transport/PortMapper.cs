using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace Yaparr.Transport;

public sealed record PortMapping(string Method, IPEndPoint ExternalEndPoint, int LeaseSeconds);

public sealed class PortMapperOptions
{
    public IPAddress? Gateway { get; init; }
    public int NatPmpPort { get; init; } = 5351;
    public IPEndPoint SsdpEndPoint { get; init; } = new(IPAddress.Parse("239.255.255.250"), 1900);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);
    public int LeaseSeconds { get; init; } = 3600;
}

public sealed class PortMapper : IDisposable
{
    private readonly int _internalPort;
    private readonly PortMapperOptions _options;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private PortMapping? _mapping;
    private string? _controlUrl, _serviceType;
    private IPAddress? _gateway;
    private int _externalPort;

    public PortMapper(int internalPort, PortMapperOptions? options = null, Action<string>? log = null)
    {
        _internalPort = internalPort;
        _options = options ?? new PortMapperOptions();
        _log = log;
    }

    public PortMapping? Mapping => _mapping;

    public async Task<PortMapping?> MapAsync(CancellationToken ct = default)
    {
        _gateway = _options.Gateway ?? FindGateway();
        if (_gateway != null)
        {
            _mapping = await TryNatPmpAsync(_gateway, ct).ConfigureAwait(false);
            if (_mapping != null) { StartRenewal(); return _mapping; }
        }

        _mapping = await TryUpnpAsync(ct).ConfigureAwait(false);
        if (_mapping != null) StartRenewal();
        return _mapping;
    }

    private void StartRenewal()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    int lease = _mapping?.LeaseSeconds ?? 0;
                    if (lease <= 0) return;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(30, lease / 2)), _cts.Token).ConfigureAwait(false);
                    var renewed = _mapping!.Method == "NAT-PMP"
                        ? await TryNatPmpAsync(_gateway!, _cts.Token).ConfigureAwait(false)
                        : await RenewUpnpAsync(_cts.Token).ConfigureAwait(false);
                    if (renewed != null) _mapping = renewed;
                    else _log?.Invoke("port mapping renewal failed");
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    internal static IPAddress? FindGateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        }
        catch (NetworkInformationException) { return null; }
    }

    private static IPAddress LocalAddressToward(IPAddress target)
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Connect(new IPEndPoint(target, 9));
        return ((IPEndPoint)probe.LocalEndPoint!).Address;
    }

    private async Task<PortMapping?> TryNatPmpAsync(IPAddress gateway, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var target = new IPEndPoint(gateway, _options.NatPmpPort);

            var external = await ExchangeAsync(udp, target, new byte[] { 0, 0 }, ct).ConfigureAwait(false);
            if (external is not { Length: >= 12 } || external[1] != 128 || BinaryPrimitives.ReadUInt16BigEndian(external.AsSpan(2)) != 0) return null;
            var address = new IPAddress(external.AsSpan(8, 4));

            var request = new byte[12];
            request[1] = 1;
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)_internalPort);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6), (ushort)(_externalPort != 0 ? _externalPort : _internalPort));
            BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(8), (uint)_options.LeaseSeconds);

            var reply = await ExchangeAsync(udp, target, request, ct).ConfigureAwait(false);
            if (reply is not { Length: >= 16 } || reply[1] != 129 || BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(2)) != 0) return null;

            _externalPort = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(10));
            int lease = (int)BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(12));
            _log?.Invoke($"NAT-PMP mapped {address}:{_externalPort}");
            return new PortMapping("NAT-PMP", new IPEndPoint(address, _externalPort), lease);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }
    }

    private async Task<byte[]?> ExchangeAsync(UdpClient udp, IPEndPoint target, byte[] request, CancellationToken ct)
    {
        var wait = TimeSpan.FromMilliseconds(Math.Max(100, _options.Timeout.TotalMilliseconds / 3));
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await udp.SendAsync(request, target, ct).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(wait);
            try
            {
                var result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                if (result.RemoteEndPoint.Address.Equals(target.Address)) return result.Buffer;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        return null;
    }

    private async Task<PortMapping?> TryUpnpAsync(CancellationToken ct)
    {
        try
        {
            var location = await DiscoverIgdAsync(ct).ConfigureAwait(false);
            if (location == null) return null;

            var description = await _http.GetStringAsync(location, ct).ConfigureAwait(false);
            if (!ParseDescription(description, location, out var control, out var service)) return null;
            _controlUrl = control;
            _serviceType = service;

            _gateway ??= IPAddress.Parse(new Uri(location).Host);
            return await RenewUpnpAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or OperationCanceledException or FormatException or System.Xml.XmlException)
        {
            return null;
        }
    }

    private async Task<PortMapping?> RenewUpnpAsync(CancellationToken ct)
    {
        if (_controlUrl == null || _serviceType == null) return null;
        var host = IPAddress.Parse(new Uri(_controlUrl).Host);
        var local = LocalAddressToward(host);
        int external = _externalPort != 0 ? _externalPort : _internalPort;

        foreach (int lease in new[] { _options.LeaseSeconds, 0 })
        {
            var added = await SoapAsync("AddPortMapping", new (string, string)[]
            {
                ("NewRemoteHost", ""),
                ("NewExternalPort", external.ToString()),
                ("NewProtocol", "UDP"),
                ("NewInternalPort", _internalPort.ToString()),
                ("NewInternalClient", local.ToString()),
                ("NewEnabled", "1"),
                ("NewPortMappingDescription", "Yaparr"),
                ("NewLeaseDuration", lease.ToString()),
            }, ct).ConfigureAwait(false);
            if (added == null) continue;

            var ipReply = await SoapAsync("GetExternalIPAddress", Array.Empty<(string, string)>(), ct).ConfigureAwait(false);
            var ipText = ipReply?.Descendants().FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress")?.Value;
            if (!IPAddress.TryParse(ipText, out var address) || address.Equals(IPAddress.Any)) return null;

            _externalPort = external;
            _log?.Invoke($"UPnP mapped {address}:{external}");
            return new PortMapping("UPnP", new IPEndPoint(address, external), lease);
        }
        return null;
    }

    internal static bool ParseDescription(string xml, string location, out string controlUrl, out string serviceType)
    {
        controlUrl = serviceType = "";
        var doc = XDocument.Parse(xml);
        var baseUri = new Uri(doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value is { Length: > 0 } b ? b : location);

        var services = doc.Descendants().Where(e => e.Name.LocalName == "service");
        foreach (var s in services)
        {
            var type = s.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceType")?.Value;
            var control = s.Elements().FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value;
            if (type == null || control == null) continue;
            if (!type.Contains("WANIPConnection") && !type.Contains("WANPPPConnection")) continue;
            controlUrl = new Uri(baseUri, control).ToString();
            serviceType = type;
            return true;
        }
        return false;
    }

    private async Task<XDocument?> SoapAsync(string action, (string Name, string Value)[] args, CancellationToken ct)
    {
        var body = new StringBuilder();
        body.Append("<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>");
        body.Append($"<u:{action} xmlns:u=\"{_serviceType}\">");
        foreach (var (name, value) in args) body.Append($"<{name}>{System.Security.SecurityElement.Escape(value)}</{name}>");
        body.Append($"</u:{action}></s:Body></s:Envelope>");

        using var request = new HttpRequestMessage(HttpMethod.Post, _controlUrl)
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8, "text/xml"),
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{_serviceType}#{action}\"");
        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return XDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException or TaskCanceledException)
        {
            return null;
        }
    }

    private async Task<string?> DiscoverIgdAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var targets = new[]
        {
            "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "upnp:rootdevice",
        };
        foreach (var st in targets)
        {
            var search = Encoding.ASCII.GetBytes(
                $"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {st}\r\n\r\n");
            await udp.SendAsync(search, _options.SsdpEndPoint, ct).ConfigureAwait(false);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.Timeout);
        try
        {
            while (true)
            {
                var result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                var text = Encoding.ASCII.GetString(result.Buffer);
                foreach (var line in text.Split("\r\n"))
                    if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                        return line["LOCATION:".Length..].Trim();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    public async Task UnmapAsync()
    {
        _cts.Cancel();
        var mapping = _mapping;
        _mapping = null;
        if (mapping == null) return;
        try
        {
            if (mapping.Method == "UPnP")
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await SoapAsync("DeletePortMapping", new (string, string)[]
                {
                    ("NewRemoteHost", ""),
                    ("NewExternalPort", mapping.ExternalEndPoint.Port.ToString()),
                    ("NewProtocol", "UDP"),
                }, cts.Token).ConfigureAwait(false);
            }
            else if (_gateway != null)
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                var request = new byte[12];
                request[1] = 1;
                BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)_internalPort);
                await udp.SendAsync(request, new IPEndPoint(_gateway, _options.NatPmpPort)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
