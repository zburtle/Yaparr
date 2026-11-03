using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yaparr.Identity;

namespace Yaparr.Chat;

/// <summary>
/// Someone a message names: the person (an account) and the name as it was written after the @ in the words, so that it can be found
/// there again even when it holds spaces or the person has since changed their name.
/// </summary>
public sealed record Mention(PeerId Account, string Name)
{
    /// <summary>What to look for in the words: the name behind an @.</summary>
    public string Tag => "@" + Name;

    /// <summary>The name that stands for every member of the room (<see cref="Everyone"/>).</summary>
    public const string EveryoneName = "everyone";

    /// <summary>
    /// Names everyone in the room. It uses an account id that no one can have (all zeros), and only counts when the author
    /// is an admin or the owner of the room (see <see cref="ChatRoom.Names"/>), so a member cannot call everyone to a message.
    /// </summary>
    public static Mention Everyone { get; } = new(new PeerId(new string('0', 64)), EveryoneName);

    public bool IsEveryone => Account == Everyone.Account;
}

/// <summary>
/// What a message says besides its words: which earlier message it answers, and which people it names.
/// <see cref="ReplyTo"/> is the <see cref="ChatMessage.Key"/> of that message.
/// </summary>
public sealed record MessageParts(string Body, string? ReplyTo, IReadOnlyList<Mention> Mentions)
{
    public static readonly MessageParts None = new("", null, Array.Empty<Mention>());
}

/// <summary>
/// Carries a reply and the people a message names inside the text of the message, behind a marker, the way an attachment is carried
/// (see <see cref="Attachment"/>). The message is signed as it always was, so the reply and the names are covered by the signature
/// and every version still accepts it; a version that does not know the marker shows it with the words.
/// </summary>
public static class MessageEnvelope
{
    private const string Marker = "\u0001yaparr-env1\u0001";
    private const char End = '\u0001';

    /// <summary>At most this many people can be named in one message.</summary>
    public const int MaxMentions = 20;

    /// <summary>The longest name that can be written after an @.</summary>
    public const int MaxMentionName = 64;

    /// <summary>The largest text a message can have (see the signed message limit); the header counts against it.</summary>
    public const int MaxText = 8000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// The text to send for <paramref name="body"/> that answers the message with key <paramref name="replyTo"/> and names
    /// <paramref name="mentions"/>. With neither, it is the body unchanged.
    /// </summary>
    public static string Wrap(string body, string? replyTo, IReadOnlyCollection<Mention>? mentions)
    {
        var named = (mentions ?? Array.Empty<Mention>()).Select(m => new Mention(m.Account, CleanName(m.Name))).Where(m => m.Name.Length > 0)
            .GroupBy(m => m.Account).Select(g => g.First()).Take(MaxMentions).ToArray();
        if (replyTo != null && !IsKey(replyTo)) throw new ArgumentException("That is not the key of a message.", nameof(replyTo));
        if (replyTo == null && named.Length == 0) return body;
        var header = JsonSerializer.Serialize(new Header { R = replyTo, M = named.Length == 0 ? null : named.Select(m => new HeaderMention { A = m.Account.Value, N = m.Name }).ToArray() }, Json);
        var text = Marker + header + End + body;
        if (text.Length > MaxText) throw new InvalidOperationException("That message is too long.");
        return text;
    }

    /// <summary>Reads a message text. Anything that is not a well formed envelope is just words.</summary>
    public static MessageParts Parse(string text)
    {
        if (text.Length == 0 || text[0] != '\u0001' || !text.StartsWith(Marker, StringComparison.Ordinal)) return new MessageParts(text, null, Array.Empty<Mention>());
        int end = text.IndexOf(End, Marker.Length);
        if (end < 0) return new MessageParts(text, null, Array.Empty<Mention>());
        try
        {
            var header = JsonSerializer.Deserialize<Header>(text.AsSpan(Marker.Length, end - Marker.Length), Json);
            if (header == null) return new MessageParts(text, null, Array.Empty<Mention>());
            var reply = header.R != null && IsKey(header.R) ? header.R.ToLowerInvariant() : null;
            var mentions = (header.M ?? Array.Empty<HeaderMention>())
                .Where(m => m.A != null && IsAccount(m.A) && CleanName(m.N ?? "").Length > 0)
                .Select(m => new Mention(new PeerId(m.A!.ToLowerInvariant()), CleanName(m.N!)))
                .GroupBy(m => m.Account).Select(g => g.First()).Take(MaxMentions).ToList();
            return new MessageParts(text[(end + 1)..], reply, mentions);
        }
        catch (JsonException) { return new MessageParts(text, null, Array.Empty<Mention>()); }
    }

    // A message key (SignedMessage.KeyOf): 32 hexadecimal digits.
    internal static bool IsKey(string s) => s.Length == 32 && s.All(Uri.IsHexDigit);

    // An account id: 64 hexadecimal digits.
    private static bool IsAccount(string s) => s is { Length: 64 } && s.All(Uri.IsHexDigit);

    // A name has no control characters, no line breaks and is not longer than the limit.
    private static string CleanName(string name)
    {
        var clean = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length <= MaxMentionName ? clean : clean[..MaxMentionName];
    }

    private sealed class Header
    {
        public string? R { get; set; }
        public HeaderMention[]? M { get; set; }
    }

    private sealed class HeaderMention
    {
        public string? A { get; set; }
        public string? N { get; set; }
    }
}
