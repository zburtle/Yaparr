using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Yaparr.Chat;

public sealed class RoomKey
{
    private const int Length = 32;
    private readonly byte[] _secret;

    private RoomKey(byte[] secret)
    {
        _secret = secret;
        Topic = SHA1.HashData(Derive("yaparr/room-topic/v1"));
        AuthKey = Derive("yaparr/room-auth/v1");
    }

    public static RoomKey Generate() => new(RandomNumberGenerator.GetBytes(Length));

    public static RoomKey Parse(string invite)
    {
        byte[] bytes;
        try { bytes = Base64Url.DecodeFromChars(invite.Trim()); }
        catch (FormatException) { throw new FormatException("Not a valid room key."); }
        if (bytes.Length != Length) throw new FormatException("Not a valid room key.");
        return new RoomKey(bytes);
    }

    internal byte[] Topic { get; }

    internal byte[] AuthKey { get; }

    public override string ToString() => Base64Url.EncodeToString(_secret);

    private byte[] Derive(string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _secret, 32, salt: null, info: Encoding.ASCII.GetBytes(purpose));
}
