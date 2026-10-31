using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Yaparr.Identity;

namespace Yaparr.Chat;

/// <summary>
/// What you need to be in a room. A room made with <see cref="Generate(PeerId)"/> also names its owner (the account that made
/// it) inside the invite, which is how everyone can tell who may appoint admins without a server. The owner is part of the
/// room's identity, so the same secret with another owner is another room. Invites without an owner (rooms made by earlier
/// versions) still work and have no admins.
/// </summary>
/// <remarks>
/// The owner can replace the secret (see <see cref="ChatRoom.RotateSecret"/>), which locks out everyone who was not given the new one.
/// The room stays the same room: its <see cref="RoomId"/> (the topic of the first secret) is what its history, admins, bans and
/// channels are tied to, and a rotated invite carries it together with a count of how many times the secret has been replaced.
/// </remarks>
public sealed class RoomKey
{
    private const int Length = 32, OriginLength = 20;
    private readonly byte[] _secret;
    private readonly byte[]? _owner;
    private readonly byte[]? _origin;

    private RoomKey(byte[] secret, byte[]? owner, byte[]? origin = null, int epoch = 0)
    {
        _secret = secret;
        _owner = owner;
        _origin = origin;
        Epoch = epoch;
        Topic = SHA1.HashData(Derive("yaparr/room-topic/v1"));
        AuthKey = Derive("yaparr/room-auth/v1");
    }

    /// <summary>A room with no owner and so no admins.</summary>
    public static RoomKey Generate() => new(RandomNumberGenerator.GetBytes(Length), null);

    /// <summary>A room owned by <paramref name="owner"/>, who is the only one who can appoint admins. The owner stays the same for the life of the room.</summary>
    public static RoomKey Generate(PeerId owner) => new(RandomNumberGenerator.GetBytes(Length), OwnerBytes(owner));

    private static byte[] OwnerBytes(PeerId owner)
    {
        try { return Convert.FromHexString(owner.Value) is { Length: Length } bytes ? bytes : throw new ArgumentException("Not an account id.", nameof(owner)); }
        catch (FormatException) { throw new ArgumentException("Not an account id.", nameof(owner)); }
    }

    public static RoomKey Parse(string invite)
    {
        byte[] bytes;
        try { bytes = Base64Url.DecodeFromChars(invite.Trim()); }
        catch (FormatException) { throw new FormatException("Not a valid room key."); }
        if (bytes.Length == Length) return new RoomKey(bytes, null);
        if (bytes.Length == Length * 2) return new RoomKey(bytes[..Length], bytes[Length..]);
        // A rotated key: secret, owner, the room's first topic, and how many times the secret has been replaced.
        if (bytes.Length == Length * 2 + OriginLength + 2)
        {
            int epoch = (bytes[^2] << 8) | bytes[^1];
            if (epoch == 0) throw new FormatException("Not a valid room key.");
            return new RoomKey(bytes[..Length], bytes[Length..(Length * 2)], bytes[(Length * 2)..(Length * 2 + OriginLength)], epoch);
        }
        throw new FormatException("Not a valid room key.");
    }

    /// <summary>The account that owns the room, or null for a room made by an earlier version.</summary>
    public PeerId? Owner => _owner == null ? null : new PeerId(Convert.ToHexString(_owner).ToLowerInvariant());

    /// <summary>How many times the owner has replaced the secret; 0 for a key that never was.</summary>
    public int Epoch { get; }

    /// <summary>What the room is called inside the app and in everything signed for it: the topic of its first secret, whatever the secret is now.</summary>
    public string RoomId => Convert.ToHexString(Id).ToLowerInvariant();

    internal byte[] Id => _origin ?? Topic;

    internal byte[] Topic { get; }

    internal byte[] AuthKey { get; }

    /// <summary>The same room under a new secret. Only the owner can make one that the room accepts.</summary>
    internal RoomKey Rotated(byte[] newSecret, int epoch) =>
        _owner == null ? throw new InvalidOperationException("A room without an owner cannot replace its secret.") : new RoomKey(newSecret, _owner, Id, epoch);

    internal byte[] SecretBytes => _secret;

    public override string ToString()
    {
        if (_owner == null) return Base64Url.EncodeToString(_secret);
        if (_origin == null) return Base64Url.EncodeToString(_secret.Concat(_owner).ToArray());
        return Base64Url.EncodeToString(_secret.Concat(_owner).Concat(_origin).Concat(new[] { (byte)(Epoch >> 8), (byte)Epoch }).ToArray());
    }

    // An unowned room derives exactly what it always did, so existing rooms keep their topic and their members.
    private byte[] Derive(string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _secret, 32, salt: null,
            info: Encoding.ASCII.GetBytes(_owner == null ? purpose : purpose + "/owner/" + Convert.ToHexString(_owner).ToLowerInvariant()));
}
