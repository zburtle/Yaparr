using System.Net;
using System.Text.Json;
using Yaparr.Identity;
using Yaparr.Transport;

namespace Yaparr.Chat;

public sealed class TraversalOptions
{
    public bool Enabled { get; init; } = true;

    public bool PortMapping { get; init; } = true;

    /// <summary>Whether apps should look for peers on the local network. Off by default for now.</summary>
    public bool LocalDiscovery { get; init; }

    public IReadOnlyList<string> StunServers { get; init; } = NatProbe.DefaultServers;

    public TurnOptions? Turn { get; init; }

    /// <summary>Whether to fall back on the relay the app ships with when no server of the user's own is set up.</summary>
    public bool UseDefaultRelay { get; init; } = true;

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
                LocalDiscovery = dto.LocalDiscovery ?? false,
                UseDefaultRelay = dto.DefaultRelay ?? true,
                StunServers = dto.Stun is { Length: > 0 } stun ? stun : NatProbe.DefaultServers,
                Turn = dto.Turn is { Server: { Length: > 0 } server }
                    ? new TurnOptions { Server = server, Username = dto.Turn.Username ?? "", Credential = SecretStore.OpenText(dto.Turn.Credential ?? ""), Enabled = dto.Turn.Enabled ?? false, Always = dto.Turn.Always ?? false }
                    : null,
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new TraversalOptions();
        }
    }

    public static void SaveDefaultRelay(string path, bool use)
    {
        var root = LoadRoot(path);
        root["defaultRelay"] = use;
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static System.Text.Json.Nodes.JsonObject LoadRoot(string path)
    {
        try
        {
            if (File.Exists(path) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) is System.Text.Json.Nodes.JsonObject existing)
                return existing;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return new System.Text.Json.Nodes.JsonObject();
    }

    public static void SaveTurn(string path, TurnOptions? turn)
    {
        var root = LoadRoot(path);
        root.Remove("turn");
        if (turn != null)
            root["turn"] = new System.Text.Json.Nodes.JsonObject
            {
                ["server"] = turn.Server,
                ["username"] = turn.Username,
                ["credential"] = SecretStore.SealText(turn.Credential),
                ["enabled"] = turn.Enabled,
                ["always"] = turn.Always,
            };
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class Settings
    {
        public bool? Enabled { get; set; }
        public bool? PortMapping { get; set; }
        public bool? LocalDiscovery { get; set; }
        public bool? DefaultRelay { get; set; }
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
