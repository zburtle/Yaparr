using System.Security.Cryptography;
using System.Text;
using Yaparr.Identity;

namespace Yaparr.Chat;

/// <summary>
/// What a room's GIF shelf takes. There is no limit on how many pictures a room has; a picture is as big as any picture can be sent (the
/// limit for a picture in a message), which also keeps what a member announces from being an unreasonable size to download.
/// </summary>
public static class GifLimits
{
    /// <summary>The largest picture on a room's shelf: the same as for a picture in a message.</summary>
    public const int MaxBytes = (int)Attachment.MaxBytes;

    /// <summary>Removed pictures remembered per person, so that the removal reaches people who were away; older ones are forgotten.</summary>
    public const int RemovedPerMember = 50;
}

/// <summary>A picture on a room's GIF shelf.</summary>
/// <param name="Available">The file itself is on this computer, so it can be shown and sent.</param>
public sealed record GifInfo(string Id, PeerId Owner, string OwnerName, string Hash, int Size, string Mime, int Width, int Height, bool Available)
{
    public bool IsGif => Mime == "image/gif";
}

/// <summary>A signed statement by a room member that a picture is on the room's GIF shelf (or has been removed from it).</summary>
public sealed record StoredGif(string From, string Pub, string Id, string OwnerName, string Hash, int Size, string Mime, int W, int H, long Ts, bool Deleted, string Sig);

internal static class SignedGif
{
    private const int MaxName = 64;
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);
    private static readonly string[] Mimes = { "image/gif", "image/png", "image/jpeg", "image/webp" };

    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    public static StoredGif Create(PeerIdentity identity, string roomKey, string id, string ownerName, string hash, int size, string mime, int width, int height, long timestamp, bool deleted)
    {
        var unsigned = new StoredGif(identity.Id.Value, Convert.ToBase64String(identity.PublicKey), id, ownerName.Length <= MaxName ? ownerName : ownerName[..MaxName], hash, size, mime, width, height, timestamp, deleted, "");
        return unsigned with { Sig = Convert.ToBase64String(identity.Sign(Payload(roomKey, unsigned))) };
    }

    public static bool Verify(StoredGif g, string roomKey)
    {
        if (g.From is null || g.Pub is null || g.Id is null || g.OwnerName is null || g.Hash is null || g.Mime is null || g.Sig is null) return false;
        if (!IsHex(g.Id, 16) || !IsHex(g.Hash, 64) || g.OwnerName.Length > MaxName) return false;
        if (!g.Deleted && (g.Size is < 1 or > GifLimits.MaxBytes || !Mimes.Contains(g.Mime)
                           || g.W is < 1 or > ImageSniffer.MaxDimension || g.H is < 1 or > ImageSniffer.MaxDimension)) return false;
        if (g.Ts > DateTimeOffset.UtcNow.Add(MaxFutureSkew).ToUnixTimeMilliseconds()) return false;
        try
        {
            var pub = Convert.FromBase64String(g.Pub);
            return PeerId.FromPublicKey(pub).Value == g.From && PeerIdentity.Verify(pub, Payload(roomKey, g), Convert.FromBase64String(g.Sig));
        }
        catch (FormatException) { return false; }
    }

    private static byte[] Payload(string roomKey, StoredGif g)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("yaparr-gif-v1"); w.Write(roomKey); w.Write(g.From); w.Write(g.Id); w.Write(g.OwnerName); w.Write(g.Hash);
            w.Write(g.Size); w.Write(g.Mime); w.Write(g.W); w.Write(g.H); w.Write(g.Ts); w.Write(g.Deleted);
        }
        return ms.ToArray();
    }

    private static bool IsHex(string s, int length) => s.Length == length && s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
