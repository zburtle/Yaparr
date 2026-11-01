using System.Text;
using System.Text.Json;

namespace Yaparr.Transport;

/// <summary>
/// A TURN relay as text, so one person can hand theirs to another: <c>yaparr-relay:</c> followed by the server, user name and
/// password. Anyone who has the code can use the relay, so it is for people you trust.
/// </summary>
public static class RelayShare
{
    public const string Prefix = "yaparr-relay:";
    private const int MaxField = 256;

    public static string Create(TurnOptions turn)
    {
        if (string.IsNullOrWhiteSpace(turn.Server)) throw new ArgumentException("The relay has no server.", nameof(turn));
        var payload = new Payload { V = 1, Server = turn.Server.Trim(), Username = turn.Username ?? "", Credential = turn.Credential ?? "" };
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Prefix + Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Reads a code made by <see cref="Create"/>. Spaces and line breaks in it are ignored, so a code that was wrapped by a mail program still works.</summary>
    public static bool TryParse(string? text, out TurnOptions? turn)
    {
        turn = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var code = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        if (!code.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var body = code[Prefix.Length..].Replace('-', '+').Replace('_', '/');
        if (body.Length == 0 || body.Length > 4 * MaxField) return false;
        try
        {
            var bytes = Convert.FromBase64String(body.PadRight(body.Length + (4 - body.Length % 4) % 4, '='));
            var payload = JsonSerializer.Deserialize<Payload>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (payload is not { V: 1 }) return false;
            var server = payload.Server?.Trim() ?? "";
            var user = payload.Username ?? "";
            var credential = payload.Credential ?? "";
            if (!Plain(server) || server.Length == 0 || !Plain(user) || !Plain(credential)) return false;
            var (host, port) = TurnClient.SplitHostPort(server, 3478);
            if (host.Length == 0 || port is < 1 or > 65535) return false;
            turn = new TurnOptions { Server = server, Username = user, Credential = credential, Enabled = true };
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    private static bool Plain(string value) => value.Length <= MaxField && !value.Any(char.IsControl);

    private sealed class Payload
    {
        public int V { get; set; }
        public string? Server { get; set; }
        public string? Username { get; set; }
        public string? Credential { get; set; }
    }
}
