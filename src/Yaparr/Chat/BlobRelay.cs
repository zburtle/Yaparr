using System.Security.Cryptography;
using System.Text;

namespace Yaparr.Chat;

/// <summary>
/// Where the locked copy of a picture is kept for people who are offline: a Yaparr Cloud server (see <c>docs/mailbox.md</c>). The app gives the node one while it is signed in to
/// a server; the node locks a picture before it is uploaded and opens it after it is downloaded, so the server only ever holds bytes it cannot read.
/// </summary>
public interface IBlobRelay
{
    /// <summary>The server's address (https, no path), as it is written into a message so a reader on the same server can fetch the picture.</summary>
    string Server { get; }

    /// <summary>Uploads locked bytes under <paramref name="id"/> (the SHA-256 of the bytes, lower case hex). False when the server did not take them.</summary>
    Task<bool> UploadAsync(string id, byte[] lockedBytes, CancellationToken cancel = default);

    /// <summary>The locked bytes of a blob, or null when the server has none (expired, or never uploaded).</summary>
    Task<byte[]?> DownloadAsync(string id, CancellationToken cancel = default);
}

/// <summary>
/// Where a picture can be fetched locked: the server it was uploaded to, the blob's id (the SHA-256 of the locked bytes), and the key that opens it. The key is only ever in the
/// message, which is sealed and signed like any other; the server never sees it.
/// </summary>
public sealed record BlobRef(string Server, string Id, string Key)
{
    internal bool IsWellFormed =>
        Server.Length is > 8 and <= 200 && Server.StartsWith("https://", StringComparison.Ordinal) && Uri.TryCreate(Server, UriKind.Absolute, out var u) && u.Scheme == "https" && u.UserInfo.Length == 0
        && Id.Length == 64 && Id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
        && Key.Length == 44 && TryKey(Key, out _);

    internal static bool TryKey(string text, out byte[] key)
    {
        key = Array.Empty<byte>();
        try { key = Convert.FromBase64String(text); return key.Length == 32; }
        catch (FormatException) { return false; }
    }
}

/// <summary>Locks a picture for upload (AES-256-GCM under a fresh key, the picture's hash as associated data so the locked bytes cannot be passed off as another picture) and opens it again.</summary>
internal static class BlobSeal
{
    private const int NonceSize = 12, TagSize = 16;

    public static (byte[] Locked, byte[] Key) Lock(byte[] plain, string hash)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var locked = new byte[NonceSize + plain.Length + TagSize];
        nonce.CopyTo(locked, 0);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, locked.AsSpan(NonceSize, plain.Length), locked.AsSpan(NonceSize + plain.Length, TagSize), Encoding.ASCII.GetBytes(hash));
        return (locked, key);
    }

    public static byte[]? Open(byte[] locked, byte[] key, string hash)
    {
        if (locked.Length <= NonceSize + TagSize) return null;
        try
        {
            var plain = new byte[locked.Length - NonceSize - TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(locked.AsSpan(0, NonceSize), locked.AsSpan(NonceSize, plain.Length), locked.AsSpan(NonceSize + plain.Length, TagSize), plain, Encoding.ASCII.GetBytes(hash));
            return plain;
        }
        catch (CryptographicException) { return null; }
    }
}

public sealed partial class ChatNode
{
    /// <summary>Where pictures are kept locked for people who are offline. Set by the app while it is signed in to a Yaparr Cloud server, null otherwise.</summary>
    public IBlobRelay? BlobRelay { get; set; }

    private static readonly TimeSpan BlobUploadTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Locks a picture made with <see cref="PrepareAttachment"/> and leaves it on the server, so a member who is offline when it is sent can still fetch it. Returns the attachment
    /// with where to fetch it from and the key; when there is no server, or it does not take the file, the attachment is returned as it was and travels between members as before.
    /// Call it before <see cref="Attachment.ToText"/>.
    /// </summary>
    public async Task<Attachment> RelayAttachmentAsync(Attachment attachment, CancellationToken ct = default)
    {
        if (BlobRelay is not { } relay || attachment.Relay != null) return attachment;
        var plain = _blobs.Read(attachment.Hash);
        if (plain == null || plain.Length != attachment.Size) return attachment;
        var (locked, key) = BlobSeal.Lock(plain, attachment.Hash);
        var id = Convert.ToHexStringLower(SHA256.HashData(locked));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(BlobUploadTimeout);
        try
        {
            if (!await relay.UploadAsync(id, locked, timeout.Token).ConfigureAwait(false)) return attachment;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _options.Log?.Invoke("could not leave a picture on the server: " + ex.Message);
            return attachment;
        }
        return attachment with { Relay = new BlobRef(relay.Server, id, Convert.ToBase64String(key)) };
    }

    // The picture, fetched locked from the server it was left on and opened. Only from the server this device is signed in to, so a message cannot make the app
    // contact a server of the sender's choosing, and only a picture whose bytes match both the id and the hash the message names is kept.
    private async Task<bool> FetchFromRelayAsync(Attachment a, CancellationToken ct)
    {
        if (a.Relay is not { IsWellFormed: true } blob || BlobRelay is not { } relay) return false;
        if (!string.Equals(blob.Server.TrimEnd('/'), relay.Server.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var locked = await relay.DownloadAsync(blob.Id, ct).ConfigureAwait(false);
            if (locked == null || Convert.ToHexStringLower(SHA256.HashData(locked)) != blob.Id) return false;
            BlobRef.TryKey(blob.Key, out var key);
            var plain = BlobSeal.Open(locked, key, a.Hash);
            if (plain == null || Attachment.HashOf(plain) != a.Hash) return false;
            _blobs.Save(a.Hash, plain);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            _options.Log?.Invoke("could not fetch a picture from the server: " + ex.Message);
            return false;
        }
    }
}
