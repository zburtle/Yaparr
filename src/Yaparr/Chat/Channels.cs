using System.Security.Cryptography;
using System.Text;
using Yaparr.Identity;

namespace Yaparr.Chat;

public enum ChannelKind { Text, Voice }

/// <summary>A channel of a room, text or voice. Every room has one of each from the start (<see cref="RoomChannels"/>).</summary>
public sealed record ChannelInfo(string Id, string Name, ChannelKind Kind, int Order, bool IsDefault);

public static class RoomChannels
{
    /// <summary>The text channel every room has, and the one messages without a channel belong to (all messages from earlier versions).</summary>
    public const string DefaultText = "0000000000000000";

    /// <summary>The voice channel every room has.</summary>
    public const string DefaultVoice = "0000000000000001";

    /// <summary>How many channels a room can have, the two it starts with included.</summary>
    public const int MaxChannels = 32;

    public static string Normalize(string? id) => string.IsNullOrEmpty(id) ? DefaultText : id;

    internal static bool IsDefault(string id) => id is DefaultText or DefaultVoice;
}

/// <summary>
/// An owner's or admin's signed statement that a channel exists with this name, or no longer does. The newest statement about
/// a channel from a current admin wins, as with the admin list. The two default channels exist without any statement, and can be
/// renamed but not removed.
/// </summary>
public sealed record StoredChannel(string Pub, string Acct, string Cert, string Id, string Name, string Kind, int Order, bool Deleted, long Ts, string Sig);

internal static class SignedChannel
{
    private const int MaxName = 64;
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);

    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    public static string KindText(ChannelKind kind) => kind == ChannelKind.Voice ? "voice" : "text";

    public static StoredChannel Create(PeerIdentity identity, string roomKey, string id, string name, ChannelKind kind, int order, bool deleted, long timestamp)
    {
        bool linked = identity.DeviceCertificate != null;
        var unsigned = new StoredChannel(Convert.ToBase64String(identity.PublicKey),
            linked ? Convert.ToBase64String(identity.AccountPublicKey) : "",
            linked ? Convert.ToBase64String(identity.DeviceCertificate!) : "",
            id, name.Length <= MaxName ? name : name[..MaxName], KindText(kind), order, deleted, timestamp, "");
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
        }
        return ms.ToArray();
    }
}
