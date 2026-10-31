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
public sealed class RoomKey
{
    private const int Length = 32;
    private readonly byte[] _secret;
    private readonly byte[]? _owner;

    private RoomKey(byte[] secret, byte[]? owner)
    {
        _secret = secret;
        _owner = owner;
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
        throw new FormatException("Not a valid room key.");
    }

    /// <summary>The account that owns the room, or null for a room made by an earlier version.</summary>
    public PeerId? Owner => _owner == null ? null : new PeerId(Convert.ToHexString(_owner).ToLowerInvariant());

    internal byte[] Topic { get; }

    internal byte[] AuthKey { get; }

    public override string ToString() => Base64Url.EncodeToString(_owner == null ? _secret : _secret.Concat(_owner).ToArray());

    // An unowned room derives exactly what it always did, so existing rooms keep their topic and their members.
    private byte[] Derive(string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _secret, 32, salt: null,
            info: Encoding.ASCII.GetBytes(_owner == null ? purpose : purpose + "/owner/" + Convert.ToHexString(_owner).ToLowerInvariant()));
}
