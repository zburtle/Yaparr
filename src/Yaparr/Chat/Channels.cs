using System.Security.Cryptography;
using System.Text;
using Yaparr.Identity;

namespace Yaparr.Chat;

public enum ChannelKind { Text, Voice }

/// <summary>
/// A channel of a room, text or voice. Every room has one of each from the start (<see cref="RoomChannels"/>). <paramref name="Category"/> is the name of the group the
/// admins put it in, empty for none (the two first channels are never in a category).
/// </summary>
public sealed record ChannelInfo(string Id, string Name, ChannelKind Kind, int Order, bool IsDefault, string Category = "");

public static class RoomChannels
{
    /// <summary>The text channel every room has, and the one messages without a channel belong to (all messages from earlier versions).</summary>
    public const string DefaultText = "0000000000000000";

    /// <summary>The voice channel every room has.</summary>
    public const string DefaultVoice = "0000000000000001";

    /// <summary>
    /// The place a room's start record is kept (see <see cref="ChatRoom.RecordStart"/>), a channel id that no channel can have: it keeps that message out of every channel's
    /// messages, so it is never shown, counted or announced.
    /// </summary>
    public const string StartMarker = "ffffffffffffffff";

    /// <summary>How many channels a room can have, the two it starts with included.</summary>
    public const int MaxChannels = 32;

    /// <summary>How long the name of a category can be.</summary>
    public const int MaxCategoryName = 32;

    public static string Normalize(string? id) => string.IsNullOrEmpty(id) ? DefaultText : id;

    /// <summary>A category name as it is kept: one line, single spaces, at most <see cref="MaxCategoryName"/> letters. Empty for none.</summary>
    public static string CleanCategory(string? name)
    {
        name = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return name.Length <= MaxCategoryName ? name : name[..MaxCategoryName].TrimEnd();
    }

    /// <summary>
    /// The categories of these channels, in the order they are shown: by the place of the first channel in each, then by name. A name is the same category whatever its capitals.
    /// </summary>
    public static List<string> CategoriesOf(IEnumerable<ChannelInfo> channels) =>
        channels.Where(c => c.Category.Length > 0)
            .GroupBy(c => c.Category, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(g => g.Min(c => c.Order)).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => g.First().Category).ToList();

    internal static bool IsDefault(string id) => id is DefaultText or DefaultVoice;
}

/// <summary>
/// An owner's or admin's signed statement that a channel exists with this name, or no longer does. The newest statement about
/// a channel from a current admin wins, as with the admin list. The two default channels exist without any statement, and can be
/// renamed but not removed. A statement that puts the channel in a category carries its name (and signs it); one without is as it always was,
/// so versions that do not know categories read it, and drop the ones that have one.
/// </summary>
public sealed record StoredChannel(string Pub, string Acct, string Cert, string Id, string Name, string Kind, int Order, bool Deleted, long Ts, string Sig, string? Category = null);

internal static class SignedChannel
{
    private const int MaxName = 64;
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);

    public static string NewId()
    {
        while (true)
        {
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            if (id != RoomChannels.StartMarker) return id;   // that one is kept for the room's start record
        }
    }

    public static string KindText(ChannelKind kind) => kind == ChannelKind.Voice ? "voice" : "text";

    public static StoredChannel Create(PeerIdentity identity, string roomKey, string id, string name, ChannelKind kind, int order, bool deleted, long timestamp, string category = "")
    {
        bool linked = identity.DeviceCertificate != null;
        var unsigned = new StoredChannel(Convert.ToBase64String(identity.PublicKey),
            linked ? Convert.ToBase64String(identity.AccountPublicKey) : "",
            linked ? Convert.ToBase64String(identity.DeviceCertificate!) : "",
            id, name.Length <= MaxName ? name : name[..MaxName], KindText(kind), order, deleted, timestamp, "", category.Length > 0 ? category : null);
        return unsigned with { Sig = Convert.ToBase64String(identity.Sign(Payload(roomKey, identity.Id, unsigned))) };
    }

    /// <summary>Whether the statement is well formed and really signed by a device of <paramref name="signer"/>. Not whether that account may change channels.</summary>
    public static bool Verify(StoredChannel c, string roomKey, out PeerId signer)
    {
        signer = default;
        if (c.Pub is null || c.Acct is null || c.Cert is null || c.Id is null || c.Name is null || c.Kind is null || c.Sig is null) return false;
        if (c.Id.Length != 16 || !c.Id.All(ch => ch is (>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
        if (c.Kind is not ("text" or "voice") || c.Name.Length > MaxName || c.Order is < 0 or > 10_000) return false;
        if (!c.Deleted && c.Name.Trim().Length == 0) return false;
        if (c.Category is { } category && (category.Length is 0 or > RoomChannels.MaxCategoryName || category.Any(char.IsControl) || RoomChannels.CleanCategory(category) != category)) return false;
        if (c.Ts > DateTimeOffset.UtcNow.Add(MaxFutureSkew).ToUnixTimeMilliseconds()) return false;
        try
        {
            var pub = Convert.FromBase64String(c.Pub);
            var acct = c.Acct.Length == 0 ? pub : Convert.FromBase64String(c.Acct);
            if (c.Acct.Length == 0 != (c.Cert.Length == 0)) return false;
            if (c.Acct.Length > 0 && !PeerIdentity.VerifyDevice(acct, pub, Convert.FromBase64String(c.Cert))) return false;
            if (!PeerIdentity.Verify(pub, Payload(roomKey, PeerId.FromPublicKey(pub), c), Convert.FromBase64String(c.Sig))) return false;
            signer = PeerId.FromPublicKey(acct);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { return false; }
    }

    private static byte[] Payload(string roomKey, PeerId device, StoredChannel c)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("yaparr-channel-v1"); w.Write(roomKey); w.Write(device.Value); w.Write(c.Id); w.Write(c.Name);
            w.Write(c.Kind); w.Write(c.Order); w.Write(c.Deleted); w.Write(c.Ts);
            if (!string.IsNullOrEmpty(c.Category)) { w.Write("category"); w.Write(c.Category); }
        }
        return ms.ToArray();
    }
}
