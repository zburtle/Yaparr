using System.Security.Cryptography;
using System.Text;

namespace Yaparr.Identity;

public readonly record struct PeerId(string Value)
{
    public static PeerId FromPublicKey(ReadOnlySpan<byte> spki) =>
        new(Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant());

    public string Short => Value.Length > 8 ? Value[..8] : Value;

    public override string ToString() => Value;
}

public sealed class PeerIdentity : IDisposable
{
    private readonly ECDsa _key;

    private PeerIdentity(ECDsa key)
    {
        _key = key;
        PublicKey = key.ExportSubjectPublicKeyInfo();
        Id = PeerId.FromPublicKey(PublicKey);
    }

    public PeerId Id { get; }

    public byte[] PublicKey { get; }

    public static PeerIdentity Generate() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static PeerIdentity Import(ReadOnlySpan<byte> pkcs8PrivateKey)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8PrivateKey, out _);
        return new PeerIdentity(key);
    }

    public byte[] ExportPrivateKey() => _key.ExportPkcs8PrivateKey();

    public byte[] Sign(ReadOnlySpan<byte> data) => _key.SignData(data, HashAlgorithmName.SHA256);

    public static bool Verify(ReadOnlySpan<byte> publicKeySpki, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException) { return false; }
    }

    public void Dispose() => _key.Dispose();
}
