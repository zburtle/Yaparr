using System.Security.Cryptography;
using System.Text;

namespace Yaparr.AppCore;

/// <summary>
/// Locks the body of a synced record, so that the server, which stores the records, can read none of them. The key is made from the account's recovery code (the same
/// code that opens a backup, which never leaves the devices): the code is 120 random bits, so it needs no slow key stretching, and a key for this job is made with HKDF under
/// a name of its own, so it is not the key that locks backups. A body is <c>1 | nonce (12) | AES-GCM(value) | tag (16)</c>, base64. The record's kind, id, clock and
/// whether it is deleted are bound to it as associated data, so the server cannot give one record's body to another, or an old body a newer clock, without it being noticed.
/// </summary>
public sealed class SyncSealer
{
    private const byte Version = 1;
    private const int NonceLength = 12, TagLength = 16, KeyLength = 32, MinCodeLength = 16;
    private readonly byte[] _key;

    public SyncSealer(byte[] key)
    {
        if (key.Length != KeyLength) throw new ArgumentException("The key is 32 bytes.", nameof(key));
        _key = (byte[])key.Clone();
    }

    /// <summary>The sealer for an account's recovery code, however the person typed it (dashes, spaces and case do not matter).</summary>
    public static SyncSealer FromRecoveryCode(string code)
    {
        var normal = new string((code ?? "").Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
        if (normal.Length < MinCodeLength) throw new ArgumentException("That is not a recovery code.", nameof(code));
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.ASCII.GetBytes(normal), KeyLength, Encoding.ASCII.GetBytes("yaparr-sync-v1"), Encoding.ASCII.GetBytes("record-key"));
        return new SyncSealer(key);
    }

    private static byte[] Aad(string kind, string id, string clock, bool deleted) => Encoding.UTF8.GetBytes($"yaparr-sync-v1\n{kind}\n{id}\n{clock}\n{(deleted ? 1 : 0)}");

    /// <summary>The body to send for a record that is not deleted (a deleted one is sent with no body).</summary>
    public string Seal(string kind, string id, string clock, byte[] value)
    {
        var output = new byte[1 + NonceLength + value.Length + TagLength];
        output[0] = Version;
        var nonce = output.AsSpan(1, NonceLength);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagLength);
        aes.Encrypt(nonce, value, output.AsSpan(1 + NonceLength, value.Length), output.AsSpan(1 + NonceLength + value.Length, TagLength), Aad(kind, id, clock, deleted: false));
        return Convert.ToBase64String(output);
    }

    /// <summary>The value in a body. A wrong key, a body that was changed and one given to the wrong record all look the same: <see cref="CryptographicException"/>.</summary>
    public byte[] Open(string kind, string id, string clock, string body)
    {
        byte[] input;
        try { input = Convert.FromBase64String(body); }
        catch (FormatException ex) { throw new CryptographicException("The body is not base64.", ex); }
        if (input.Length < 1 + NonceLength + TagLength || input[0] != Version) throw new CryptographicException("The body is not one this version made.");
        var value = new byte[input.Length - 1 - NonceLength - TagLength];
        using var aes = new AesGcm(_key, TagLength);
        aes.Decrypt(input.AsSpan(1, NonceLength), input.AsSpan(1 + NonceLength, value.Length), input.AsSpan(1 + NonceLength + value.Length, TagLength), value, Aad(kind, id, clock, deleted: false));
        return value;
    }
}
