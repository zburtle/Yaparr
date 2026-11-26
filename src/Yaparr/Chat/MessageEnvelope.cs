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
public sealed record MessageParts(string Body, string? ReplyTo, IReadOnlyList<Mention> Mentions, LinkPreview? Preview = null, ReactionEvent? Reaction = null, IReadOnlyList<CustomEmoji>? Emoji = null, Poll? Poll = null)
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
    public static string Wrap(string body, string? replyTo, IReadOnlyCollection<Mention>? mentions, LinkPreview? preview = null, IReadOnlyCollection<CustomEmoji>? emoji = null, Poll? poll = null)
    {
        var named = (mentions ?? Array.Empty<Mention>()).Select(m => new Mention(m.Account, CleanName(m.Name))).Where(m => m.Name.Length > 0)
            .GroupBy(m => m.Account).Select(g => g.First()).Take(MaxMentions).ToArray();
        if (replyTo != null && !IsKey(replyTo)) throw new ArgumentException("That is not the key of a message.", nameof(replyTo));
        var card = preview?.Normalized();
        // The custom emoji the words use, each once, within the limits: what is carried is only what the words show.
        var written = CustomEmojiRules.NamesIn(body);
        var used = (emoji ?? Array.Empty<CustomEmoji>()).Where(e => CustomEmojiRules.IsAcceptable(e) && written.Contains(e.Name))
            .GroupBy(e => e.Name).Select(g => g.First()).Take(CustomEmojiRules.MaxPerMessage).ToArray();
        // A poll is asked in words that a version without polls shows as they are (the question and the numbered answers).
        if (poll != null) body = poll.Words;
        if (replyTo == null && named.Length == 0 && card == null && used.Length == 0 && poll == null) return body;
        // A card that does not fit loses its picture first, then goes; the words and the people they name always come first.
        foreach (var candidate in card == null ? new LinkPreview?[] { null } : new[] { card, card with { Thumbnail = null }, null })
        {
            var header = JsonSerializer.Serialize(new Header
            {
                R = replyTo, M = named.Length == 0 ? null : named.Select(m => new HeaderMention { A = m.Account.Value, N = m.Name }).ToArray(),
                E = used.Length == 0 ? null : used.Select(e => new HeaderEmoji { N = e.Name, H = e.Hash, S = e.Size, M = e.Mime, W = e.Width, G = e.Height }).ToArray(),
                L = poll == null ? null : new HeaderPoll { Q = poll.Question, O = poll.Options.ToArray(), M = poll.Multiple ? 1 : null },
                P = candidate == null ? null : new HeaderPreview { U = candidate.Url, T = candidate.Title, D = candidate.Description.Length == 0 ? null : candidate.Description, I = candidate.Thumbnail is { Length: > 0 } th ? Convert.ToBase64String(th) : null },
            }, Json);
            if (replyTo == null && named.Length == 0 && candidate == null && used.Length == 0 && poll == null) return body;
            var text = Marker + header + End + body;
            if (text.Length <= MaxText) return text;
        }
        throw new InvalidOperationException("That message is too long.");
    }

    /// <summary>
    /// The text to send for putting <paramref name="emoji"/> on the message with key <paramref name="target"/> (<see cref="ChatMessage.Key"/>), or taking it off
    /// again. It is a message of its own that is not shown as one (see <see cref="ReactionEvent"/>); the words are only for versions that do not know it.
    /// </summary>
    public static string WrapReaction(string target, string emoji, bool on, byte[]? accountKey = null, byte[]? deviceCertificate = null, CustomEmoji? custom = null)
    {
        if (!IsKey(target)) throw new ArgumentException("That is not the key of a message.", nameof(target));
        string standard;
        if (custom != null)
        {
            // A custom emoji as the reaction: its description travels with the reaction, so everyone can fetch and show the picture. A version that does not know this
            // finds no standard emoji in it and ignores the reaction.
            if (!CustomEmojiRules.IsAcceptable(custom)) throw new ArgumentException("That is not a custom emoji that can be used as a reaction.", nameof(custom));
            standard = CustomKey(custom);
        }
        else if (!EmojiData.TryNormalize(emoji, out standard!)) throw new ArgumentException("That is not an emoji that can be used as a reaction.", nameof(emoji));
        var header = JsonSerializer.Serialize(new Header { X = new HeaderReaction
        {
            T = target.ToLowerInvariant(), E = standard, O = on ? null : 0,
            K = custom == null ? null : new HeaderEmoji { N = custom.Name, H = custom.Hash, S = custom.Size, M = custom.Mime, W = custom.Width, G = custom.Height },
            // From a linked device: the proof of whose it is, so that every device can tell the reaction is the account's even if it has never met this one.
            A = accountKey != null && deviceCertificate != null ? Convert.ToBase64String(accountKey) : null,
            C = accountKey != null && deviceCertificate != null ? Convert.ToBase64String(deviceCertificate) : null,
        } }, Json);
        return Marker + header + End + (on ? "reacted " : "took back ") + (custom != null ? ":" + custom.Name + ":" : standard);
    }

    // The header of a reaction starts with its field, so a reaction can be told from any other message without reading it all.
    private static readonly string ReactionStart = Marker + "{\"x\":";

    /// <summary>Whether a message text is a reaction (see <see cref="WrapReaction"/>), told cheaply. It is not checked to be a good one.</summary>
    public static bool IsReactionText(string text) => text.StartsWith(ReactionStart, StringComparison.Ordinal);

    /// <summary>Reads a reaction out of a message text: false when it is not one, or its message key or emoji are not good.</summary>
    public static bool TryReadReaction(string text, out ReactionEvent reaction)
    {
        reaction = null!;
        if (!IsReactionText(text)) return false;
        var parts = Parse(text);
        if (parts.Reaction == null) return false;
        reaction = parts.Reaction;
        return true;
    }

    /// <summary>
    /// The text of a room's start record: the owner's statement, in a message of its own that is not shown as one, that the room began when the message was written (see
    /// <see cref="ChatRoom.RecordStart"/>). Like a reaction from a linked device it carries the proof of whose it is (the account key and the device's certificate), so that
    /// anyone can tell it is the owner's without having met that device. The words are only for versions that do not know it.
    /// </summary>
    public static string WrapStart(PeerIdentity identity)
    {
        bool linked = identity.DeviceCertificate != null;
        var header = JsonSerializer.Serialize(new Header { S = new HeaderStart
        {
            A = linked ? Convert.ToBase64String(identity.AccountPublicKey) : null,
            C = linked ? Convert.ToBase64String(identity.DeviceCertificate!) : null,
        } }, Json);
        return Marker + header + End + "started this room";
    }

    private static readonly string StartHeader = Marker + "{\"s\":";

    /// <summary>Whether a message text is a room's start record (see <see cref="WrapStart"/>), told cheaply. It is not checked to be a good one.</summary>
    public static bool IsStartText(string text) => text.StartsWith(StartHeader, StringComparison.Ordinal);

    /// <summary>Reads the proof out of a start record: the account key and certificate of a linked device (both null for the account's own key). False when it is not a start record.</summary>
    public static bool TryReadStart(string text, out string? account, out string? certificate)
    {
        account = certificate = null;
        if (!IsStartText(text)) return false;
        int end = text.IndexOf(End, Marker.Length);
        if (end < 0) return false;
        try
        {
            var start = JsonSerializer.Deserialize<Header>(text.AsSpan(Marker.Length, end - Marker.Length), Json)?.S;
            if (start == null) return false;
            account = start.A is { Length: <= 200 } ? start.A : null;
            certificate = start.C is { Length: <= 400 } ? start.C : null;
            return true;
        }
        catch (JsonException) { return false; }
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
            var preview = ReadPreview(header.P);
            var reaction = ReadReaction(header.X);
            var mentions = (header.M ?? Array.Empty<HeaderMention>())
                .Where(m => m.A != null && IsAccount(m.A) && CleanName(m.N ?? "").Length > 0)
                .Select(m => new Mention(new PeerId(m.A!.ToLowerInvariant()), CleanName(m.N!)))
                .GroupBy(m => m.Account).Select(g => g.First()).Take(MaxMentions).ToList();
            var body = text[(end + 1)..];
            // A card is for a link that is in the words; a card for some other address is not shown.
            if (preview != null && !body.Contains(preview.Url, StringComparison.Ordinal)) preview = null;
            return new MessageParts(body, reply, mentions, preview, reaction, ReadEmoji(header.E, body), ReadPoll(header.L));
        }
        catch (JsonException) { return new MessageParts(text, null, Array.Empty<Mention>()); }
    }

    // A card from the header, held to the same limits as one that is sent. Anything that does not hold is left out; the words are unaffected.
    private static LinkPreview? ReadPreview(HeaderPreview? p)
    {
        if (p?.U == null) return null;
        byte[]? thumb = null;
        if (p.I is { Length: > 0 and <= LinkPreview.MaxThumbnailBytes * 2 })
        {
            try { thumb = Convert.FromBase64String(p.I); } catch (FormatException) { }
        }
        return new LinkPreview(p.U, p.T ?? "", p.D ?? "", thumb).Normalized();
    }

    // A poll from the header, held to the limits of one that is sent; anything that does not hold is not a poll (the words are still shown).
    private static Poll? ReadPoll(HeaderPoll? l) =>
        l?.Q == null || l.O == null || l.O.Length > Poll.MaxOptions * 2 ? null : Poll.Clean(l.Q, l.O, l.M == 1);

    // The custom emoji from the header: only those that are within the limits and that the words actually use, each name once.
    private static IReadOnlyList<CustomEmoji>? ReadEmoji(HeaderEmoji[]? list, string body)
    {
        if (list == null || list.Length == 0) return null;
        var written = CustomEmojiRules.NamesIn(body);
        var read = list.Take(CustomEmojiRules.MaxPerMessage * 2)
            .Where(e => e?.N != null && e.H != null && e.M != null)
            .Select(e => new CustomEmoji(e.N!, e.H!.ToLowerInvariant(), e.S, e.M!, e.W, e.G))
            .Where(e => CustomEmojiRules.IsAcceptable(e) && written.Contains(e.Name))
            .GroupBy(e => e.Name).Select(g => g.First()).Take(CustomEmojiRules.MaxPerMessage).ToList();
        return read.Count == 0 ? null : read;
    }

    // A reaction from the header: a message key and an emoji of the set, or nothing.
    private static ReactionEvent? ReadReaction(HeaderReaction? x)
    {
        if (x?.T == null || !IsKey(x.T)) return null;
        string? account = x.A is { Length: <= 200 } ? x.A : null, certificate = x.C is { Length: <= 400 } ? x.C : null;
        // A custom emoji: held to the same limits as one in a message; anything that does not hold is not a reaction.
        if (x.K is { } k)
        {
            if (k.N == null || k.H == null || k.M == null) return null;
            var custom = new CustomEmoji(k.N, k.H.ToLowerInvariant(), k.S, k.M, k.W, k.G);
            return CustomEmojiRules.IsAcceptable(custom) ? new ReactionEvent(x.T.ToLowerInvariant(), CustomKey(custom), x.O != 0, account, certificate, custom) : null;
        }
        return EmojiData.TryNormalize(x.E, out var emoji) ? new ReactionEvent(x.T.ToLowerInvariant(), emoji, x.O != 0, account, certificate) : null;
    }

    /// <summary>What names a custom emoji as a reaction: its name and the hash of its picture (so a name that two people gave to two pictures is two reactions).</summary>
    public static string CustomKey(CustomEmoji emoji) => ":" + emoji.Name + ":" + emoji.Hash;

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

    private sealed class HeaderStart
    {
        public string? A { get; set; }   // the account's public key (a linked device's start record)
        public string? C { get; set; }   // the device's certificate
    }

    private sealed class Header
    {
        public HeaderReaction? X { get; set; }   // first, see ReactionStart
        public HeaderStart? S { get; set; }      // second, see StartHeader (a start record has nothing else)
        public string? R { get; set; }
        public HeaderMention[]? M { get; set; }
        public HeaderPreview? P { get; set; }
        public HeaderEmoji[]? E { get; set; }   // custom emoji the words use
        public HeaderPoll? L { get; set; }      // the poll this message asks
    }

    private sealed class HeaderPoll
    {
        public string? Q { get; set; }     // the question
        public string?[]? O { get; set; }  // the answers
        public int? M { get; set; }        // 1 when more than one answer can be chosen
    }

    private sealed class HeaderEmoji
    {
        public string? N { get; set; }   // the name, as in :name:
        public string? H { get; set; }   // the hash of the picture
        public int S { get; set; }       // its size in bytes
        public string? M { get; set; }   // its type
        public int W { get; set; }       // its width
        public int G { get; set; }       // its height
    }

    private sealed class HeaderReaction
    {
        public string? T { get; set; }
        public string? E { get; set; }
        public int? O { get; set; }   // 0 when it is taken off
        public string? A { get; set; }   // the account key of a linked device
        public string? C { get; set; }   // the account's certificate for that device
        public HeaderEmoji? K { get; set; }   // a custom emoji used as the reaction
    }

    private sealed class HeaderPreview
    {
        public string? U { get; set; }
        public string? T { get; set; }
        public string? D { get; set; }
        public string? I { get; set; }
    }

    private sealed class HeaderMention
    {
        public string? A { get; set; }
        public string? N { get; set; }
    }
}
