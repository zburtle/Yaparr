using System.Security.Cryptography;
using System.Text;
using Yaparr.Identity;

namespace Yaparr.Chat;

public static class PlaylistLimits
{
    /// <summary>How many items a room's playlist holds.</summary>
    public const int MaxItems = 100;

    public const int MaxTitle = 120, MaxSubtitle = 80, MaxId = 64, MaxName = 40, MaxBody = 2000;
}

/// <summary>
/// One thing in a room's playlist: a film or an episode on the streamer's Jellyfin server (<see cref="ServerId"/> says which server, since an id means something on that one only),
/// with what is needed to show it, and who put it there (<see cref="AddedBy"/> is that person's account).
/// </summary>
public sealed record PlaylistItem(string Id, string ServerId, string ItemId, string Title, string Subtitle, string Kind, long RuntimeTicks, string? SeriesId, string AddedBy, string AddedByName, long AddedTs)
{
    public bool IsEpisode => Kind == "episode";
}

/// <summary>What a person picks to add: the same as an item, before it has a place, a server and a person's name on it.</summary>
public sealed record PlaylistDraft(string ItemId, string Title, string Subtitle, string Kind, long RuntimeTicks, string? SeriesId);

/// <summary>The room's playlist as it stands: the items in the order they play, who is streaming from which server, and whether others may add.</summary>
public sealed record PlaylistSnapshot(long Version, string ServerId, string HostName, bool OthersMayAdd, IReadOnlyList<PlaylistItem> Items, string HostId = "")
{
    public static PlaylistSnapshot Empty { get; } = new(0, "", "", false, Array.Empty<PlaylistItem>());
}

/// <summary>
/// The playlist as the streamer signs it: the whole list each time, and the newest version wins. Only the person who is streaming makes one; everyone else sends them a request
/// (<see cref="StoredAsk"/>). It is kept on every member's computer, so that it is there for the next stream in the room.
/// </summary>
public sealed record StoredPlaylist(string From, string Pub, string HostName, string ServerId, long Version, bool OthersMayAdd, PlaylistItem[] Items, string Sig);

/// <summary>
/// A request to the person who is streaming: to add something, to remove something one added, or (to browse the library to find something) a question about it. It is signed by
/// the person asking, since it may pass through other members on its way.
/// </summary>
public sealed record StoredAsk(string Pub, string Acct, string Cert, string To, string Id, string Op, string Name, string Body, long Ts, string Sig);

/// <summary>A question about the streamer's library from someone who may add to the playlist. <see cref="Respond"/> sends back the answer.</summary>
public sealed class PlaylistBrowseRequest
{
    private readonly Action<string> _respond;

    internal PlaylistBrowseRequest(string body, string who, Action<string> respond)
    {
        Body = body;
        Who = who;
        _respond = respond;
    }

    /// <summary>What was asked (the app that streams knows how to read it).</summary>
    public string Body { get; }

    /// <summary>The name of the person who asked.</summary>
    public string Who { get; }

    public void Respond(string answer) => _respond(answer);
}

internal static class PlaylistRules
{
    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    public static string Clean(string? text, int max)
    {
        var line = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        line = new string(line.Where(c => !char.IsControl(c)).ToArray());
        return line.Length <= max ? line : line[..max].TrimEnd();
    }

    public static bool IsKind(string? kind) => kind is "movie" or "episode";

    public static bool IsValidDraft(PlaylistDraft d) =>
        d.ItemId is { Length: > 0 and <= PlaylistLimits.MaxId } && d.Title is { Length: > 0 and <= PlaylistLimits.MaxTitle } && d.Subtitle is { Length: <= PlaylistLimits.MaxSubtitle }
        && IsKind(d.Kind) && d.RuntimeTicks >= 0 && (d.SeriesId is null || d.SeriesId.Length is > 0 and <= PlaylistLimits.MaxId);

    public static bool IsValidItem(PlaylistItem i) =>
        i.Id is { Length: > 0 and <= PlaylistLimits.MaxId } && i.ServerId is { Length: <= PlaylistLimits.MaxId } && i.AddedBy is { Length: > 0 and <= PlaylistLimits.MaxId }
        && i.AddedByName is { Length: <= PlaylistLimits.MaxName } && i.AddedTs >= 0
        && IsValidDraft(new PlaylistDraft(i.ItemId, i.Title, i.Subtitle, i.Kind, i.RuntimeTicks, i.SeriesId));
}

internal static class SignedPlaylist
{
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);

    public static StoredPlaylist Create(PeerIdentity identity, string roomKey, string hostName, string serverId, long version, bool othersMayAdd, PlaylistItem[] items)
    {
        var unsigned = new StoredPlaylist(identity.Id.Value, Convert.ToBase64String(identity.PublicKey), PlaylistRules.Clean(hostName, PlaylistLimits.MaxName), serverId, version, othersMayAdd, items, "");
        return unsigned with { Sig = Convert.ToBase64String(identity.Sign(Payload(roomKey, unsigned))) };
    }

    public static bool Verify(StoredPlaylist s, string roomKey)
    {
        if (s.From is null || s.Pub is null || s.HostName is null || s.ServerId is null || s.Items is null || s.Sig is null) return false;
        if (s.HostName.Length > PlaylistLimits.MaxName || s.ServerId.Length > PlaylistLimits.MaxId || s.Version < 0) return false;
        if (s.Items.Length > PlaylistLimits.MaxItems || s.Items.Any(i => i is null || !PlaylistRules.IsValidItem(i)) || s.Items.Select(i => i.Id).Distinct().Count() != s.Items.Length) return false;
        if (s.Version > DateTimeOffset.UtcNow.Add(MaxFutureSkew).ToUnixTimeMilliseconds()) return false;
        try
        {
            var pub = Convert.FromBase64String(s.Pub);
            return PeerId.FromPublicKey(pub).Value == s.From && PeerIdentity.Verify(pub, Payload(roomKey, s), Convert.FromBase64String(s.Sig));
        }
        catch (FormatException) { return false; }
    }

    private static byte[] Payload(string roomKey, StoredPlaylist s)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("yaparr-playlist-v1"); w.Write(roomKey); w.Write(s.From); w.Write(s.HostName); w.Write(s.ServerId); w.Write(s.Version); w.Write(s.OthersMayAdd); w.Write(s.Items.Length);
            foreach (var i in s.Items)
            {
                w.Write(i.Id); w.Write(i.ServerId); w.Write(i.ItemId); w.Write(i.Title); w.Write(i.Subtitle); w.Write(i.Kind); w.Write(i.RuntimeTicks);
                w.Write(i.SeriesId ?? ""); w.Write(i.AddedBy); w.Write(i.AddedByName); w.Write(i.AddedTs);
            }
        }
        return ms.ToArray();
    }
}

internal static class SignedAsk
{
    private static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);

    public static StoredAsk Create(PeerIdentity identity, string roomKey, string to, string id, string op, string name, string body, long timestamp)
    {
        bool linked = identity.DeviceCertificate != null;
        var unsigned = new StoredAsk(Convert.ToBase64String(identity.PublicKey),
            linked ? Convert.ToBase64String(identity.AccountPublicKey) : "",
            linked ? Convert.ToBase64String(identity.DeviceCertificate!) : "",
            to, id, op, PlaylistRules.Clean(name, PlaylistLimits.MaxName), body, timestamp, "");
        return unsigned with { Sig = Convert.ToBase64String(identity.Sign(Payload(roomKey, unsigned))) };
    }

    /// <summary>Whether the request is well formed, recent and really signed by a device of <paramref name="account"/> (<paramref name="device"/> is the device that signed it).</summary>
    public static bool Verify(StoredAsk a, string roomKey, out PeerId account, out PeerId device)
    {
        account = device = default;
        if (a.Pub is null || a.Acct is null || a.Cert is null || a.To is null || a.Id is null || a.Op is null || a.Name is null || a.Body is null || a.Sig is null) return false;
        if (a.Op is not ("add" or "remove" or "browse") || a.Id.Length is 0 or > PlaylistLimits.MaxId || a.Name.Length > PlaylistLimits.MaxName || a.Body.Length > PlaylistLimits.MaxBody) return false;
        if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - a.Ts) > MaxSkew.TotalMilliseconds) return false;
        try
        {
            var pub = Convert.FromBase64String(a.Pub);
            var acct = a.Acct.Length == 0 ? pub : Convert.FromBase64String(a.Acct);
            if (a.Acct.Length == 0 != (a.Cert.Length == 0)) return false;
            if (a.Acct.Length > 0 && !PeerIdentity.VerifyDevice(acct, pub, Convert.FromBase64String(a.Cert))) return false;
            if (!PeerIdentity.Verify(pub, Payload(roomKey, a), Convert.FromBase64String(a.Sig))) return false;
            device = PeerId.FromPublicKey(pub);
            account = PeerId.FromPublicKey(acct);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { return false; }
    }

    private static byte[] Payload(string roomKey, StoredAsk a)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("yaparr-playlist-ask-v1"); w.Write(roomKey); w.Write(a.To); w.Write(a.Id); w.Write(a.Op); w.Write(a.Name); w.Write(a.Body); w.Write(a.Ts);
        }
        return ms.ToArray();
    }
}
