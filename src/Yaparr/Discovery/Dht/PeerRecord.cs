using System.Net;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Math.EC.Rfc8032;
using Yaparr.Identity;

namespace Yaparr.Discovery.Dht;

/// <summary>
/// BEP 44 mutable items: small signed values stored in the DHT under an ed25519 key, where a newer sequence
/// number replaces the older value. The DHT checks the ed25519 signature, so only the holder of the key can write.
/// </summary>
public static class MutableItem
{
    public const int MaxValueLength = 1000;
    public const int MaxSaltLength = 64;

    public static byte[] Target(byte[] publicKey, byte[] salt) => SHA1.HashData([.. publicKey, .. salt]);

    /// <summary>The exact bytes that are signed: salt, sequence number and the bencoded value (BEP 44).</summary>
    public static byte[] SignedBytes(byte[] salt, long seq, byte[] value)
    {
        using var ms = new MemoryStream();
        if (salt.Length > 0)
        {
            ms.Write(Encoding.ASCII.GetBytes("4:salt" + salt.Length + ":"));
            ms.Write(salt);
        }
        ms.Write(Encoding.ASCII.GetBytes("3:seqi" + seq + "e1:v" + value.Length + ":"));
        ms.Write(value);
        return ms.ToArray();
    }

    public static byte[] PublicKeyFromSeed(byte[] seed)
    {
        var pk = new byte[Ed25519.PublicKeySize];
        Ed25519.GeneratePublicKey(seed, 0, pk, 0);
        return pk;
    }

    public static byte[] Sign(byte[] seed, byte[] publicKey, byte[] salt, long seq, byte[] value)
    {
        var message = SignedBytes(salt, seq, value);
        var sig = new byte[Ed25519.SignatureSize];
        Ed25519.Sign(seed, 0, message, 0, message.Length, sig, 0);
        return sig;
    }

    public static bool Verify(byte[] publicKey, byte[] salt, long seq, byte[] value, byte[] signature)
    {
        if (publicKey.Length != Ed25519.PublicKeySize || signature.Length != Ed25519.SignatureSize) return false;
        var message = SignedBytes(salt, seq, value);
        try { return Ed25519.Verify(signature, 0, publicKey, 0, message, 0, message.Length); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
    }
}

/// <summary>A value found in the DHT and checked against its signature.</summary>
public sealed record FoundItem(byte[] Value, long Seq, byte[] Signature);

/// <summary>
/// Where a peer can be reached right now, published under a key everyone can work out from the peer's ID.
///
/// Because the DHT key can be derived by anyone who knows the ID, the DHT's own signature only keeps strangers from
/// writing by accident; what makes a record trustworthy is the inner signature made with the peer's real identity
/// key, which readers check against the ID. A forged or overwritten record is simply ignored.
/// </summary>
public sealed class PeerRecord
{
    public const int MaxEndPoints = 12;
    private const string Context = "yaparr-peer-record-v1";
    private static readonly byte[] Salt = Encoding.ASCII.GetBytes("yaparr");

    public required PeerId Peer { get; init; }
    public required IReadOnlyList<IPEndPoint> EndPoints { get; init; }
    public required long Time { get; init; }

    public static byte[] SaltBytes => Salt;

    /// <summary>The ed25519 key under which a peer's record is stored; derived from the peer ID so others can find it.</summary>
    public static (byte[] Seed, byte[] PublicKey) DeriveKey(PeerId peer)
    {
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes("yaparr-dht-record-key-v1:" + peer.Value));
        return (seed, MutableItem.PublicKeyFromSeed(seed));
    }

    public static byte[] Encode(PeerIdentity identity, IEnumerable<IPEndPoint> endPoints, long time)
    {
        var list = endPoints.Where(e => e.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Distinct().Take(MaxEndPoints).ToList();
        var compact = new byte[list.Count * 6];
        for (int i = 0; i < list.Count; i++)
        {
            list[i].Address.GetAddressBytes().CopyTo(compact, i * 6);
            compact[i * 6 + 4] = (byte)(list[i].Port >> 8);
            compact[i * 6 + 5] = (byte)list[i].Port;
        }
        var record = new BDict
        {
            ["k"] = identity.PublicKey,
            ["e"] = compact,
            ["t"] = time,
            ["s"] = identity.Sign(SignedPart(compact, time)),
        };
        // Stored as an opaque byte string so the bytes that were signed are exactly the bytes that come back.
        return Bencode.Encode(record);
    }

    /// <summary>Checks the record against the peer it is supposed to describe; null if it is not valid.</summary>
    public static PeerRecord? Decode(PeerId expected, ReadOnlySpan<byte> value)
    {
        try
        {
            if (Bencode.Decode(value) is not BDict d) return null;
            if (d.Bytes("k") is not { } key || d.Bytes("e") is not { } compact || d.Bytes("s") is not { } sig || d.Int("t") is not { } time) return null;
            if (compact.Length % 6 != 0 || compact.Length / 6 > MaxEndPoints) return null;
            if (PeerId.FromPublicKey(key) != expected) return null;
            if (!PeerIdentity.Verify(key, SignedPart(compact, time), sig)) return null;

            var endPoints = new List<IPEndPoint>();
            for (int i = 0; i < compact.Length; i += 6)
            {
                int port = (compact[i + 4] << 8) | compact[i + 5];
                if (port != 0) endPoints.Add(new IPEndPoint(new IPAddress(compact.AsSpan(i, 4)), port));
            }
            return new PeerRecord { Peer = expected, EndPoints = endPoints, Time = time };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException) { return null; }
    }

    private static byte[] SignedPart(byte[] compact, long time) =>
        [.. Encoding.ASCII.GetBytes(Context), .. BitConverter.GetBytes(time), .. compact];
}
