using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yaparr.Chat;

/// <summary>
/// An invitation to a room, sent to one person in a direct chat: the room's key (what lets them in) and the name the sender knows it by. It travels behind a marker in the text of the message, like a
/// file offer, so the message is signed and encrypted like any other and an older version just shows it as text. Only a direct chat is taken as an invitation: the same text in a room is plain words.
/// Receiving one asks for nothing and joins nothing; the person decides (see the notification list).
/// </summary>
public sealed record RoomInvite(string Key, string Name)
{
    private const string Marker = "\u0001yaparr-invite1\u0001";

    /// <summary>The longest room name an invitation carries.</summary>
    public const int MaxName = 60;

    /// <summary>The message text that carries this invitation.</summary>
    public string ToText() => Marker + JsonSerializer.Serialize(new Dto { K = Key, N = Name }, Json);

    /// <summary>The room's id, which names it the same whatever key it is under (see <see cref="RoomKey.RoomId"/>).</summary>
    public string RoomId => RoomKey.Parse(Key).RoomId;

    /// <summary>Recognises a message that invites to a room. Anything malformed, or with a key that is not one, is treated as plain text.</summary>
    public static bool TryParse(string? text, out RoomInvite invite)
    {
        invite = null!;
        if (text == null || !text.StartsWith(Marker, StringComparison.Ordinal)) return false;
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(text.AsSpan(Marker.Length), Json);
            if (dto?.K is not { Length: > 0 and <= 400 } key) return false;
            RoomKey.Parse(key.Trim());
            invite = new RoomInvite(key.Trim(), CleanName(dto.N));
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException) { return false; }
    }

    /// <summary>A name that is safe to show: one line, no control characters, at most <see cref="MaxName"/> characters; "a room" when there is nothing left.</summary>
    public static string CleanName(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > MaxName) clean = clean[..MaxName].TrimEnd();
        return clean.Length == 0 ? "a room" : clean;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed class Dto
    {
        public string? K { get; set; }
        public string? N { get; set; }
    }
}
