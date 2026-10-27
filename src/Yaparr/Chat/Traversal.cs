using System.Net;
using System.Text.Json;
using Yaparr.Transport;

namespace Yaparr.Chat;

public sealed class TraversalOptions
{
    public bool Enabled { get; init; } = true;

    public bool PortMapping { get; init; } = true;

    public IReadOnlyList<string> StunServers { get; init; } = NatProbe.DefaultServers;

    public TurnOptions? Turn { get; init; }

    public static TraversalOptions Load(string path)
    {
        if (!File.Exists(path)) return new TraversalOptions();
        try
        {
            var dto = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (dto == null) return new TraversalOptions();
            return new TraversalOptions
            {
                Enabled = dto.Enabled ?? true,
                PortMapping = dto.PortMapping ?? true,
                StunServers = dto.Stun is { Length: > 0 } stun ? stun : NatProbe.DefaultServers,
                Turn = dto.Turn is { Server: { Length: > 0 } server }
                    ? new TurnOptions { Server = server, Username = dto.Turn.Username ?? "", Credential = dto.Turn.Credential ?? "", Enabled = dto.Turn.Enabled ?? false, Always = dto.Turn.Always ?? false }
                    : null,
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new TraversalOptions();
        }
    }

    private sealed class Settings
    {
        public bool? Enabled { get; set; }
        public bool? PortMapping { get; set; }
        public string[]? Stun { get; set; }
        public TurnSettings? Turn { get; set; }
    }

    private sealed class TurnSettings
    {
        public string? Server { get; set; }
        public string? Username { get; set; }
        public string? Credential { get; set; }
        public bool? Enabled { get; set; }
        public bool? Always { get; set; }
    }
}

public sealed record NetworkStatus(NatReport Nat, PortMapping? Mapping, IPEndPoint? Relay, string? Vpn = null)
{
    public static NetworkStatus Unknown { get; } = new(NatReport.Unknown, null, null);

    public bool Reachable => Mapping != null || Relay != null || Nat.Type is NatType.Open or NatType.EndpointIndependent;

    public string Describe()
    {
        var parts = new List<string>();
        if (Vpn != null) parts.Add(Vpn);
        parts.Add(Nat.Describe());
        if (Mapping != null) parts.Add($"{Mapping.Method} port {Mapping.ExternalEndPoint.Port} open");
        if (Relay != null) parts.Add($"relay {Relay}");
        return string.Join("; ", parts);
    }
}
