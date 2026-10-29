using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yaparr.Chat;

namespace Yaparr.Identity;

/// <summary>What a new device receives when it is linked to an account.</summary>
public sealed record LinkedProfile(PeerIdentity Identity, string DisplayName, IReadOnlyList<AccountEntry> Entries);

/// <summary>
/// Links another device to your account without any server. The device you already use makes an identity for the
/// new one (its own key, vouched for by the account key) and packs it with your name, rooms and blocks into a code,
/// encrypted with a passphrase. Type the code and the passphrase on the new device and it is you, as a second device.
/// The code holds your account key, so treat it like a password: the passphrase should travel separately and the
/// code should be deleted once used. A code stops working after a day.
/// </summary>
public static class DeviceLink
{
    private const string Prefix = "YLNK1.";
    private const int SaltLength = 16, NonceLength = 12, TagLength = 16, KeyLength = 32, Iterations = 600_000;
    private static readonly byte[] Context = Encoding.ASCII.GetBytes("yaparr-link-v1");
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private sealed record Payload(int V, long At, string Identity, string Name, AccountEntry[] Entries);

    /// <summary>A passphrase with about 100 bits of randomness, in groups of four to read out loud: <c>K7QD-M3XF-9PLR-2WTE-HV8N</c>.</summary>
    public static string GeneratePassphrase()
    {
        var chars = new char[5 * 4];
        for (int i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return string.Join('-', Enumerable.Range(0, 5).Select(g => new string(chars, g * 4, 4)));
    }

    /// <summary>Makes the code for a new device. Needs a device that holds the account key.</summary>
    public static string Create(PeerIdentity current, string displayName, IReadOnlyList<AccountEntry> entries, string passphrase) =>
        CreateAt(current, displayName, entries, passphrase, DateTimeOffset.UtcNow);

    internal static string CreateAt(PeerIdentity current, string displayName, IReadOnlyList<AccountEntry> entries, string passphrase, DateTimeOffset at)
    {
        using var device = current.CreateDevice();
        var payload = new Payload(1, at.ToUnixTimeSeconds(), Convert.ToBase64String(device.Export()), displayName, entries.ToArray());
        var plain = JsonSerializer.SerializeToUtf8Bytes(payload, Json);

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagLength];
        using (var aes = new AesGcm(DeriveKey(passphrase, salt), TagLength)) aes.Encrypt(nonce, plain, cipher, tag, Context);

        return Prefix + Base64Url.EncodeToString([.. salt, .. nonce, .. tag, .. cipher]);
    }

    /// <summary>
    /// Opens a code on the new device. Throws <see cref="CryptographicException"/> for a wrong passphrase or a damaged
    /// code, <see cref="FormatException"/> if it is not a link code, and <see cref="InvalidOperationException"/> if it expired.
    /// </summary>
    public static LinkedProfile Open(string code, string passphrase)
    {
        code = new string((code ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (!code.StartsWith(Prefix, StringComparison.Ordinal)) throw new FormatException("This is not a Yaparr link code.");
        byte[] raw;
        try { raw = Base64Url.DecodeFromChars(code[Prefix.Length..]); }
        catch (FormatException) { throw new FormatException("This is not a Yaparr link code."); }
        if (raw.Length < SaltLength + NonceLength + TagLength + 2) throw new FormatException("This link code is incomplete.");

        var salt = raw[..SaltLength];
        var nonce = raw[SaltLength..(SaltLength + NonceLength)];
        var tag = raw[(SaltLength + NonceLength)..(SaltLength + NonceLength + TagLength)];
        var cipher = raw[(SaltLength + NonceLength + TagLength)..];
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(DeriveKey(passphrase, salt), TagLength))
        {
            try { aes.Decrypt(nonce, cipher, tag, plain, Context); }
            catch (AuthenticationTagMismatchException) { throw new CryptographicException("The passphrase is wrong, or the code was damaged."); }
        }

        Payload? payload;
        try { payload = JsonSerializer.Deserialize<Payload>(plain, Json); }
        catch (JsonException) { throw new FormatException("This link code is not valid."); }
        if (payload is not { V: 1 }) throw new FormatException("This link code is from a newer version of Yaparr.");
        if (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(payload.At) > MaxAge)
            throw new InvalidOperationException("This link code has expired. Make a new one on your other device.");

        var identity = PeerIdentity.Import(Convert.FromBase64String(payload.Identity));
        return new LinkedProfile(identity, payload.Name, payload.Entries ?? Array.Empty<AccountEntry>());
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt)
    {
        // Case, spaces and dashes do not matter, so it can be read out loud or typed in any way.
        var normal = new string((passphrase ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(normal), salt, Iterations, HashAlgorithmName.SHA256, KeyLength);
    }
}
