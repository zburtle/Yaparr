using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Yaparr.AppCore;

/// <summary>One saved copy of a backup on a backup server.</summary>
public sealed record ServerBackupVersion(int Version, long Size, DateTimeOffset At);

/// <summary>A backup server refused, or could not be reached. The message is fit to show the person.</summary>
public class BackupServerException(string message, HttpStatusCode? status = null, Exception? inner = null) : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>Someone else (or this person on another device) saved a newer backup than the one this save was based on.</summary>
public sealed class BackupServerConflictException(int newestVersion) : BackupServerException("A newer backup is on the server. Restore from it first, or save again to replace it.", HttpStatusCode.Conflict)
{
    public int NewestVersion { get; } = newestVersion;
}

/// <summary>
/// Keeps a locked profile backup on a backup server, so it can be fetched from any device. The person types the server's address, a username and a
/// password. The password never leaves the device: it is turned into a key (Argon2id, with the username as the salt) that signs each request, and
/// the server holds only the matching public key. The same password locks the backup itself (<see cref="AppData.ExportProfile"/>), so the server
/// stores something it cannot read. How the server and the app talk is in <c>docs/backup-server.md</c>.
/// </summary>
public sealed class BackupServerClient : IDisposable
{
    public const int MinPasswordLength = 12;
    private const int Argon2MemoryKiB = 64 * 1024, Argon2Passes = 3, Argon2Lanes = 2;
    private static readonly string EmptyHash = Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>()));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _user;
    private readonly byte[] _seed;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>The address as it is used: https unless it is a local address, with no path, or null if it cannot be used.</summary>
    public static Uri? ParseServerAddress(string? typed)
    {
        var text = (typed ?? "").Trim();
        if (text.Length == 0) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return null;
        if (uri.Scheme == Uri.UriSchemeHttps) { }
        else if (uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))) { }
        else return null;   // a password-derived key is signed over, not sent, but backups and names should not cross the internet in the clear
        return new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port).Uri;
    }

    /// <summary>The form a username is kept in: trimmed and lower case, 3 to 32 letters, digits, dots, dashes and underscores. Null if it cannot be used.</summary>
    public static string? NormalizeUsername(string? typed)
    {
        var name = (typed ?? "").Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        if (name.Length is < 3 or > 32) return null;
        if (!name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' or '_')) return null;
        return char.IsLetterOrDigit(name[0]) && char.IsLetterOrDigit(name[^1]) ? name : null;
    }

    public static string? CheckPassword(string? password) =>
        (password ?? "").Length < MinPasswordLength ? $"The password needs at least {MinPasswordLength} characters." : null;

    public BackupServerClient(Uri server, string username, string password, HttpClient? http = null)
        : this(server, username, SigningSeed(username, password, Argon2MemoryKiB, Argon2Passes), http, () => DateTimeOffset.UtcNow) { }

    internal BackupServerClient(Uri server, string username, byte[] seed, HttpClient? http, Func<DateTimeOffset> now)
    {
        _user = NormalizeUsername(username) ?? throw new ArgumentException("That is not a usable username.", nameof(username));
        _seed = seed;
        _now = now;
        _ownsHttp = http == null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.BaseAddress = server;
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_seed);
        if (_ownsHttp) _http.Dispose();
    }

    /// <summary>The key that signs for this username and password. It can be made again on any device.</summary>
    internal static byte[] SigningSeed(string username, string password, int memoryKiB, int passes)
    {
        var name = NormalizeUsername(username) ?? throw new ArgumentException("That is not a usable username.", nameof(username));
        if (CheckPassword(password) is { } why) throw new ArgumentException(why, nameof(password));

        var salt = SHA256.HashData(Encoding.UTF8.GetBytes("yaparr-login-v1:" + name));
        var parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
            .WithVersion(Argon2Parameters.Version13).WithMemoryAsKB(memoryKiB).WithIterations(passes).WithParallelism(Argon2Lanes).WithSalt(salt).Build();
        var argon = new Argon2BytesGenerator();
        argon.Init(parameters);
        var master = new byte[32];
        argon.GenerateBytes(Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC)), master);
        try { return HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt: null, info: "yaparr-signing-key-v1"u8.ToArray()); }
        finally { CryptographicOperations.ZeroMemory(master); }
    }

    internal static byte[] PublicKeyOf(byte[] seed) => new Ed25519PrivateKeyParameters(seed, 0).GeneratePublicKey().GetEncoded();

    // The four headers that prove who sent a request. The signed text is the username, the method, the path and query, the time and the body's hash.
    internal static Dictionary<string, string> Sign(byte[] seed, string user, string method, string pathAndQuery, long time, string contentHash)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(seed, 0));
        var text = Encoding.UTF8.GetBytes($"YAPSVC2\n{user}\n{method.ToUpperInvariant()}\n{pathAndQuery}\n{time}\n{contentHash}");
        signer.BlockUpdate(text, 0, text.Length);
        return new Dictionary<string, string>
        {
            ["X-Yaparr-User"] = user,
            ["X-Yaparr-Time"] = time.ToString(),
            ["X-Yaparr-Content-Sha256"] = contentHash,
            ["X-Yaparr-Sig"] = Base64Url.EncodeToString(signer.GenerateSignature()),
        };
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string? contentHash = null, int? basedOn = null)
    {
        var request = new HttpRequestMessage(method, path);
        foreach (var (name, value) in Sign(_seed, _user, method.Method, path, _now().ToUnixTimeSeconds(), contentHash ?? EmptyHash))
            request.Headers.Add(name, value);
        if (basedOn != null) request.Headers.IfMatch.Add(new EntityTagHeaderValue("\"" + basedOn + "\""));
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        try { return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel); }
        catch (HttpRequestException ex) { throw new BackupServerException("Couldn't reach the backup server: " + ex.Message, inner: ex); }
        catch (TaskCanceledException ex) when (!cancel.IsCancellationRequested) { throw new BackupServerException("The backup server took too long to answer.", inner: ex); }
    }

    // What the server said went wrong, in its own words where it gave some.
    private static async Task<BackupServerException> FailureAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        string? message = null;
        try { message = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel)).RootElement.GetProperty("error").GetString(); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
        message ??= $"The backup server answered {(int)response.StatusCode} {response.ReasonPhrase}.";
        if (response.StatusCode == HttpStatusCode.Unauthorized && !message.Contains("clock", StringComparison.OrdinalIgnoreCase))
            message = "The username or password is wrong, or this server has no account for it.";
        return new BackupServerException(message, response.StatusCode);
    }

    /// <summary>The saved copies of the backup, newest first (empty if there is none). Also what proves the username and password are right.</summary>
    public async Task<IReadOnlyList<ServerBackupVersion>> VersionsAsync(CancellationToken cancel = default)
    {
        using var response = await SendAsync(Request(HttpMethod.Get, "/v1/backup/versions"), cancel);
        if (!response.IsSuccessStatusCode) throw await FailureAsync(response, cancel);
        var list = await response.Content.ReadFromJsonAsync<List<VersionJson>>(Json, cancel) ?? new();
        return list.Select(v => new ServerBackupVersion(v.Version, v.Size, v.At)).OrderByDescending(v => v.Version).ToList();
    }

    private sealed record VersionJson(int Version, long Size, DateTimeOffset At);

    /// <summary>
    /// Saves a locked backup file as the newest version and returns its number. <paramref name="basedOn"/> is the newest version this device knew
    /// of (0 for none): if someone saved another since, it is refused with <see cref="BackupServerConflictException"/> instead of replacing it.
    /// </summary>
    public async Task<int> UploadAsync(string lockedBackupPath, int basedOn, CancellationToken cancel = default)
    {
        if (!ProfileCipherIsLocked(lockedBackupPath)) throw new InvalidOperationException("Only a locked backup is sent to a server.");
        string hash;
        await using (var file = new FileStream(lockedBackupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancel));

        await using var content = new FileStream(lockedBackupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var request = Request(HttpMethod.Put, "/v1/backup", hash, basedOn);
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentLength = content.Length;
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await SendAsync(request, cancel);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            int newest = 0;
            try { newest = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel)).RootElement.GetProperty("version").GetInt32(); }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
            throw new BackupServerConflictException(newest);
        }
        if (!response.IsSuccessStatusCode) throw await FailureAsync(response, cancel);
        return (await response.Content.ReadFromJsonAsync<VersionJson>(Json, cancel))?.Version ?? basedOn + 1;
    }

    /// <summary>Fetches a backup (the newest unless <paramref name="version"/> is given) into a file and returns its version number.</summary>
    public async Task<int> DownloadAsync(string destinationPath, int? version = null, CancellationToken cancel = default)
    {
        using var request = Request(HttpMethod.Get, version == null ? "/v1/backup" : $"/v1/backup?version={version}");
        using var response = await SendAsync(request, cancel);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new BackupServerException("This account has no backup on the server yet.", response.StatusCode);
        if (!response.IsSuccessStatusCode) throw await FailureAsync(response, cancel);
        try
        {
            await using (var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                await response.Content.CopyToAsync(output, cancel);
        }
        catch { try { File.Delete(destinationPath); } catch (IOException) { } throw; }
        return response.Headers.TryGetValues("X-Yaparr-Version", out var v) && int.TryParse(v.FirstOrDefault(), out var n) ? n : version ?? 0;
    }

    /// <summary>Removes the account's backups from the server.</summary>
    public async Task DeleteAsync(CancellationToken cancel = default)
    {
        using var response = await SendAsync(Request(HttpMethod.Delete, "/v1/backup"), cancel);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        if (!response.IsSuccessStatusCode) throw await FailureAsync(response, cancel);
    }

    /// <summary>
    /// Changes the password on the server: it is given the key the new password leads to, signed with the current one. The caller then saves the
    /// backup again, locked with the new password (the old backups on the server stay locked with the old one).
    /// </summary>
    public async Task ChangePasswordAsync(string newPassword, CancellationToken cancel = default)
    {
        var newKey = PublicKeyOf(SigningSeed(_user, newPassword, Argon2MemoryKiB, Argon2Passes));
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { publicKey = Base64Url.EncodeToString(newKey) }));
        using var request = Request(HttpMethod.Put, "/v1/account/key", Convert.ToHexStringLower(SHA256.HashData(body)));
        request.Content = new ByteArrayContent(body);
        using var response = await SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode) throw await FailureAsync(response, cancel);
    }

    private static bool ProfileCipherIsLocked(string path) => Yaparr.Identity.ProfileCipher.IsLocked(path);
}
