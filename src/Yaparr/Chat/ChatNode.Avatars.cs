using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yaparr.Identity;

namespace Yaparr.Chat;

/// <summary>
/// One device saying which picture its person uses: the picture's hash, signed by the device (and, for a linked device, with the account's certificate
/// for it). The picture itself is fetched separately from whoever told us, and kept only if it matches the hash. A claim with no hash takes the picture away.
/// </summary>
public sealed record StoredAvatar(string From, string Pub, string Hash, long Ts, string Sig, string? Acct = null, string? Cert = null);

/// <summary>A picture sent in answer to a request: its hash, and the bytes in base 64.</summary>
public sealed record AvatarBlob(string Hash, string Data);

// Profile pictures are small (the apps shrink them to about 128 pixels) and are passed from neighbour to neighbour like presence: a claim of which
// picture a person uses goes to everyone we are connected to, and is handed on when it is news. The picture follows only when someone asks for it.
public sealed partial class ChatNode
{
    /// <summary>The largest picture accepted, in bytes.</summary>
    public const int MaxAvatarBytes = 32 * 1024;

    private const int MaxAvatarClaims = 1000, ClaimsPerMessage = 64;
    private static readonly TimeSpan AvatarAskAgain = TimeSpan.FromSeconds(30);

    private readonly object _avatarGate = new();
    private readonly Dictionary<PeerId, StoredAvatar> _avatarClaims = new();
    private readonly ConcurrentDictionary<string, byte[]> _avatarData = new();
    private readonly ConcurrentDictionary<string, long> _avatarAsked = new();

    /// <summary>The picture of a person changed (their account), or arrived.</summary>
    public event Action<PeerId>? AvatarChanged;

    /// <summary>The picture of a person (an account), as the bytes of an image file; null when they have none, or it has not arrived yet.</summary>
    public byte[]? AvatarOf(PeerId account)
    {
        string? hash;
        lock (_avatarGate) hash = _avatarClaims.TryGetValue(account, out var claim) && claim.Hash.Length > 0 ? claim.Hash : null;
        return hash != null && _avatarData.TryGetValue(hash, out var data) ? data : null;
    }

    /// <summary>Your own picture: what your account uses, as set on this or another of your devices.</summary>
    public byte[]? MyAvatar => AvatarOf(AccountId);

    /// <summary>Whether the bytes begin like a PNG, JPEG, GIF or WebP file, and are small enough.</summary>
    public static bool LooksLikeAvatar(ReadOnlySpan<byte> data)
    {
        if (data.Length is 0 or > MaxAvatarBytes) return false;
        return data.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 })
            || data.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })
            || data.StartsWith("GIF8"u8)
            || data.Length > 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8);
    }

    private static string HashOfAvatar(byte[] data) => Convert.ToHexString(SHA256.HashData(data))[..32].ToLowerInvariant();

    private static byte[] AvatarPayload(string hash, long ts) => Encoding.UTF8.GetBytes("yaparr/avatar/v1|" + hash + "|" + ts);

    /// <summary>
    /// Sets your picture (an image file of at most <see cref="MaxAvatarBytes"/> bytes), or takes it away (null). Your other devices and the people you are
    /// connected to hear of it.
    /// </summary>
    public void SetAvatar(byte[]? image)
    {
        if (image != null && !LooksLikeAvatar(image)) throw new ArgumentException("That is not a picture that can be used (PNG, JPEG, GIF or WebP, up to 32 KB).", nameof(image));
        string hash = image == null ? "" : HashOfAvatar(image);
        if (image != null) _avatarData[hash] = image;
        long ts = NextTimestamp();
        var claim = new StoredAvatar(Id.Value, Convert.ToBase64String(_identity.PublicKey), hash, ts, Convert.ToBase64String(_identity.Sign(AvatarPayload(hash, ts))));
        if (_identity.DeviceCertificate != null)
            claim = claim with { Acct = Convert.ToBase64String(_identity.AccountPublicKey), Cert = Convert.ToBase64String(_identity.DeviceCertificate) };
        lock (_avatarGate) _avatarClaims[AccountId] = claim;
        SaveAvatars();
        foreach (var peer in _peers.Values) Send(peer.Connection, new Wire { Type = Wire.Avatar, Avs = new[] { claim } });
        AvatarChanged?.Invoke(AccountId);
    }

    // A claim that checks out: its device key is its sender's, the signature is the device's, and it is not from the far future. Gives the account.
    private bool TryVerifyAvatar(StoredAvatar claim, out PeerId account)
    {
        account = default;
        if (claim.Hash.Length is not (0 or 32) || claim.Hash.Any(c => !Uri.IsHexDigit(c)) || claim.Pub.Length > 200 || claim.Sig.Length > 200) return false;
        if (claim.Ts > DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds()) return false;
        try
        {
            var pub = Convert.FromBase64String(claim.Pub);
            var device = PeerId.FromPublicKey(pub);
            if (device.Value != claim.From || !PeerIdentity.Verify(pub, AvatarPayload(claim.Hash, claim.Ts), Convert.FromBase64String(claim.Sig))) return false;
            account = device;
            if (claim.Acct != null && claim.Cert != null && claim.Acct.Length <= 200 && claim.Cert.Length <= 400)
            {
                var accountKey = Convert.FromBase64String(claim.Acct);
                if (!PeerIdentity.VerifyDevice(accountKey, pub, Convert.FromBase64String(claim.Cert))) return false;
                account = PeerId.FromPublicKey(accountKey);
            }
            return true;
        }
        catch (FormatException) { return false; }
    }

    private void OnAvatarClaims(PeerState from, StoredAvatar[] claims)
    {
        var news = new List<StoredAvatar>();
        var changed = new List<PeerId>();
        var ask = new List<string>();
        foreach (var claim in claims.Take(ClaimsPerMessage))
        {
            if (!TryVerifyAvatar(claim, out var account) || IsBlocked(account)) continue;
            bool isNews;
            lock (_avatarGate)
            {
                bool known = _avatarClaims.TryGetValue(account, out var old);
                isNews = !known || claim.Ts > old!.Ts;
                if (isNews)
                {
                    if (!known && _avatarClaims.Count >= MaxAvatarClaims) continue;
                    _avatarClaims[account] = claim;
                }
                else if (old!.Ts != claim.Ts || old.Hash != claim.Hash) continue;
            }
            if (isNews) { news.Add(claim); changed.Add(account); }
            // The picture, when we do not have it (or asked and got nothing): ask the one who told us, not again for a while.
            if (claim.Hash.Length > 0 && !_avatarData.ContainsKey(claim.Hash)
                && (!_avatarAsked.TryGetValue(claim.Hash, out var asked) || Environment.TickCount64 - asked > AvatarAskAgain.TotalMilliseconds))
            {
                _avatarAsked[claim.Hash] = Environment.TickCount64;
                ask.Add(claim.Hash);
            }
        }
        if (ask.Count > 0) Send(from.Connection, new Wire { Type = Wire.AvatarGet, AvIds = ask.ToArray() });
        if (news.Count > 0)
        {
            SaveAvatars();
            foreach (var peer in _peers.Values)
                if (peer.Id != from.Id) Send(peer.Connection, new Wire { Type = Wire.Avatar, Avs = news.ToArray() });
            foreach (var account in changed) AvatarChanged?.Invoke(account);
        }
    }

    private void OnAvatarGet(PeerState from, string[] ids)
    {
        var blobs = new List<AvatarBlob>();
        foreach (var id in ids.Take(ClaimsPerMessage))
            if (id.Length == 32 && _avatarData.TryGetValue(id, out var data)) blobs.Add(new AvatarBlob(id, Convert.ToBase64String(data)));
        if (blobs.Count > 0) Send(from.Connection, new Wire { Type = Wire.AvatarData, AvBlobs = blobs.ToArray() });
    }

    private void OnAvatarData(AvatarBlob[] blobs)
    {
        var changed = new List<PeerId>();
        foreach (var blob in blobs.Take(ClaimsPerMessage))
        {
            if (blob.Data.Length > MaxAvatarBytes * 2) continue;
            byte[] data;
            try { data = Convert.FromBase64String(blob.Data); }
            catch (FormatException) { continue; }
            if (!LooksLikeAvatar(data) || HashOfAvatar(data) != blob.Hash) continue;
            // Only a picture somebody has claimed.
            List<PeerId> owners;
            lock (_avatarGate) owners = _avatarClaims.Where(kv => kv.Value.Hash == blob.Hash).Select(kv => kv.Key).ToList();
            if (owners.Count == 0) continue;
            if (_avatarData.Count >= 2 * MaxAvatarClaims) _avatarData.Clear();
            _avatarData[blob.Hash] = data;
            changed.AddRange(owners);
        }
        if (changed.Count == 0) return;
        SaveAvatars();
        foreach (var account in changed) AvatarChanged?.Invoke(account);
    }

    // To someone who has just connected: everything we know of who has which picture.
    private void SendAvatars(PeerState peer)
    {
        List<StoredAvatar> all;
        lock (_avatarGate) all = _avatarClaims.Values.ToList();
        for (int i = 0; i < all.Count; i += ClaimsPerMessage)
            Send(peer.Connection, new Wire { Type = Wire.Avatar, Avs = all.Skip(i).Take(ClaimsPerMessage).ToArray() });
    }

    private void LoadAvatars()
    {
        var dir = _options.AvatarDirectory;
        if (dir == null || !Directory.Exists(dir)) return;
        try
        {
            var file = Path.Combine(dir, "claims.json");
            if (File.Exists(file))
                foreach (var claim in JsonSerializer.Deserialize<StoredAvatar[]>(File.ReadAllBytes(file), Json) ?? Array.Empty<StoredAvatar>())
                {
                    if (!TryVerifyAvatar(claim, out var account)) continue;
                    lock (_avatarGate) _avatarClaims[account] = claim;
                    if (claim.Hash.Length > 0 && File.Exists(Path.Combine(dir, claim.Hash + ".img")))
                    {
                        var data = File.ReadAllBytes(Path.Combine(dir, claim.Hash + ".img"));
                        if (LooksLikeAvatar(data) && HashOfAvatar(data) == claim.Hash) _avatarData[claim.Hash] = data;
                    }
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _options.Log?.Invoke("could not read the saved profile pictures: " + ex.Message);
        }
    }

    private void SaveAvatars()
    {
        var dir = _options.AvatarDirectory;
        if (dir == null) return;
        try
        {
            Directory.CreateDirectory(dir);
            List<StoredAvatar> claims;
            lock (_avatarGate) claims = _avatarClaims.Values.ToList();
            var keep = new HashSet<string>();
            foreach (var claim in claims.Where(c => c.Hash.Length > 0))
            {
                keep.Add(claim.Hash + ".img");
                var path = Path.Combine(dir, claim.Hash + ".img");
                if (!File.Exists(path) && _avatarData.TryGetValue(claim.Hash, out var data)) File.WriteAllBytes(path, data);
            }
            File.WriteAllBytes(Path.Combine(dir, "claims.json"), JsonSerializer.SerializeToUtf8Bytes(claims, Json));
            foreach (var old in Directory.EnumerateFiles(dir, "*.img").Where(f => !keep.Contains(Path.GetFileName(f)))) File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _options.Log?.Invoke("could not save the profile pictures: " + ex.Message);
        }
    }
}
