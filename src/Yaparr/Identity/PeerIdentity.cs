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

/// <summary>
/// Who this app instance is. Every instance has its own device key, and its <see cref="Id"/> is what the network sees.
/// A person's <see cref="AccountId"/> is the id other people know them by. On the first device the two are the
/// same key. A device linked later has a key of its own plus a certificate from the account key, so everyone it
/// meets can tell that it belongs to the same person.
/// </summary>
public sealed class PeerIdentity : IDisposable
{
    private const string CertificateContext = "yaparr-device-v1";
    private static readonly byte[] ProfileMagic = Encoding.ASCII.GetBytes("YID2");

    private readonly ECDsa _key;
    private readonly ECDsa? _accountKey;
    private readonly bool _ownsAccountKey;

    private PeerIdentity(ECDsa key, ECDsa? accountKey, bool ownsAccountKey, byte[] accountPublicKey, byte[]? certificate)
    {
        _key = key;
        _accountKey = accountKey;
        _ownsAccountKey = ownsAccountKey;
        PublicKey = key.ExportSubjectPublicKeyInfo();
        Id = PeerId.FromPublicKey(PublicKey);
        AccountPublicKey = accountPublicKey;
        AccountId = PeerId.FromPublicKey(accountPublicKey);
        DeviceCertificate = certificate;
    }

    /// <summary>This device. Connections, rooms and messages are signed with its key.</summary>
    public PeerId Id { get; }

    public byte[] PublicKey { get; }

    /// <summary>The person this device belongs to. The same as <see cref="Id"/> on the first device.</summary>
    public PeerId AccountId { get; }

    public byte[] AccountPublicKey { get; }

    /// <summary>The account's signature over this device's key. Null on a device whose key is the account key.</summary>
    public byte[]? DeviceCertificate { get; }

    /// <summary>Whether this device holds the account key, and so can link further devices.</summary>
    public bool CanLinkDevices => _accountKey != null;

    public static PeerIdentity Generate()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new PeerIdentity(key, key, ownsAccountKey: false, key.ExportSubjectPublicKeyInfo(), certificate: null);
    }

    /// <summary>
    /// Reads an identity saved by <see cref="Export"/>. Files from earlier versions hold a bare private key, which
    /// is read as an account whose only device is this one.
    /// </summary>
    public static PeerIdentity Import(ReadOnlySpan<byte> data)
    {
        if (data.Length > ProfileMagic.Length && data[..ProfileMagic.Length].SequenceEqual(ProfileMagic)) return ImportProfile(data[ProfileMagic.Length..]);
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(data, out _);
        return new PeerIdentity(key, key, ownsAccountKey: false, key.ExportSubjectPublicKeyInfo(), certificate: null);
    }

    private static PeerIdentity ImportProfile(ReadOnlySpan<byte> data)
    {
        int p = 0;
        var devicePkcs8 = ReadBlock(data, ref p);
        var accountPublic = ReadBlock(data, ref p);
        var accountPkcs8 = ReadBlock(data, ref p);
        var certificate = ReadBlock(data, ref p);

        var key = ECDsa.Create();
        ECDsa? account = null;
        try
        {
            key.ImportPkcs8PrivateKey(devicePkcs8, out _);
            if (accountPkcs8.Length > 0)
            {
                account = ECDsa.Create();
                account.ImportPkcs8PrivateKey(accountPkcs8, out _);
                accountPublic = account.ExportSubjectPublicKeyInfo();
            }
            if (!VerifyDevice(accountPublic, key.ExportSubjectPublicKeyInfo(), certificate))
                throw new CryptographicException("The device certificate does not match the account.");
            return new PeerIdentity(key, account, ownsAccountKey: true, accountPublic, certificate);
        }
        catch
        {
            key.Dispose();
            account?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// What to save to disk. A device whose key is the account key is written as the bare private key, exactly as
    /// before, so an older version can still read the file.
    /// </summary>
    public byte[] Export()
    {
        if (DeviceCertificate == null) return _key.ExportPkcs8PrivateKey();
        using var ms = new MemoryStream();
        ms.Write(ProfileMagic);
        WriteBlock(ms, _key.ExportPkcs8PrivateKey());
        WriteBlock(ms, AccountPublicKey);
        WriteBlock(ms, _accountKey?.ExportPkcs8PrivateKey() ?? Array.Empty<byte>());
        WriteBlock(ms, DeviceCertificate);
        return ms.ToArray();
    }

    /// <summary>The device key alone, as earlier versions saved it.</summary>
    public byte[] ExportPrivateKey() => _key.ExportPkcs8PrivateKey();

    /// <summary>
    /// Makes the identity for another device of the same person: a new key of its own, vouched for by the account key.
    /// Needs the account key, which every linked device holds.
    /// </summary>
    public PeerIdentity CreateDevice()
    {
        if (_accountKey == null) throw new InvalidOperationException("This device does not hold the account key, so it cannot link another device.");
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var certificate = _accountKey.SignData(CertificatePayload(key.ExportSubjectPublicKeyInfo()), HashAlgorithmName.SHA256);
            var account = ECDsa.Create();
            account.ImportPkcs8PrivateKey(_accountKey.ExportPkcs8PrivateKey(), out _);
            return new PeerIdentity(key, account, ownsAccountKey: true, AccountPublicKey, certificate);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>Whether <paramref name="certificate"/> is the account's signature over this device key.</summary>
    public static bool VerifyDevice(ReadOnlySpan<byte> accountPublicKey, ReadOnlySpan<byte> devicePublicKey, ReadOnlySpan<byte> certificate) =>
        certificate.Length > 0 && Verify(accountPublicKey, CertificatePayload(devicePublicKey), certificate);

    private static byte[] CertificatePayload(ReadOnlySpan<byte> devicePublicKey) =>
        [.. Encoding.ASCII.GetBytes(CertificateContext), .. devicePublicKey];

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

    public void Dispose()
    {
        _key.Dispose();
        if (_ownsAccountKey) _accountKey?.Dispose();
    }

    private static void WriteBlock(Stream s, byte[] data)
    {
        Span<byte> len = stackalloc byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)data.Length);
        s.Write(len);
        s.Write(data);
    }

    private static byte[] ReadBlock(ReadOnlySpan<byte> data, ref int pos)
    {
        int len = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
        pos += 2;
        var block = data.Slice(pos, len).ToArray();
        pos += len;
        return block;
    }
}
