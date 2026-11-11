using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yaparr.Identity;

namespace Yaparr.AppCore;

/// <summary>One saved copy of a backup on a backup server.</summary>
public sealed record ServerBackupVersion(int Version, long Size, DateTimeOffset At);

/// <summary>A backup server refused, or could not be reached. The message is fit to show the person.</summary>
public class BackupServerException(string message, HttpStatusCode? status = null, Exception? inner = null) : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;

    /// <summary>True when the server no longer knows this device (signed out, the password was changed, or the account is gone).</summary>
    public bool SignedOut => Status == HttpStatusCode.Unauthorized;
}

/// <summary>Someone else (or this person on another device) saved a newer backup than the one this save was based on.</summary>
public sealed class BackupServerConflictException(int newestVersion) : BackupServerException("A newer backup is on the server. Try again.", HttpStatusCode.Conflict)
{
    public int NewestVersion { get; } = newestVersion;
}

/// <summary>
/// What this device remembers about its backup server (<c>backup-server.json</c>, sealed like the other files with secrets in them, and not part of
/// a backup): where it is, who is signed in, the token that proves it, and the recovery code that locks the backups.
/// </summary>
public sealed class BackupServerState
{
    public string Url { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>What the server gave this device when the person signed in. Empty when signed out.</summary>
    public string Token { get; set; } = "";
    /// <summary>What locks the backups (see <see cref="RecoveryCode"/>). It never goes to the server.</summary>
    public string RecoveryCode { get; set; } = "";
    /// <summary>The version this device last saved or restored (0 if none), to tell when another device has saved a newer one.</summary>
    public int Version { get; set; }

    // ---- automatic backups (see AutoBackup) ----

    /// <summary>Back up by itself: when what cannot be lost changes, and once a day for the history. Off until the person turns it on.</summary>
    public bool AutoBackup { get; set; }
    /// <summary>When the last backup (by hand or automatic) was saved.</summary>
    public DateTimeOffset? LastBackup { get; set; }
    /// <summary>The keys, rooms, name and account as they were at that backup (<see cref="AppData.KeysFingerprint"/>), to tell when they have changed since.</summary>
    public string KeysFingerprint { get; set; } = "";
    /// <summary>Another device saved a newer backup: automatic backups wait until the person has backed up by hand (which asks).</summary>
    public bool AutoPaused { get; set; }
    /// <summary>The last thing automatic backups had to say, for the Backup page: what they did, or why they stopped.</summary>
    public string AutoNote { get; set; } = "";
    /// <summary>Failed attempts in a row, and when the first of them was, and when to try again.</summary>
    public int Failures { get; set; }
    public DateTimeOffset? FirstFailure { get; set; }
    public DateTimeOffset? RetryAfter { get; set; }

    /// <summary>Records a backup that was saved, by hand or by itself.</summary>
    public void RecordBackup(int version, string keysFingerprint, DateTimeOffset now)
    {
        Version = version;
        LastBackup = now;
        KeysFingerprint = keysFingerprint;
        AutoPaused = false;
        Failures = 0;
        FirstFailure = null;
        RetryAfter = null;
    }

    public bool SignedIn => Token.Length > 0 && Url.Length > 0;
}

/// <summary>
/// The code that locks a backup kept on a server. The app makes it (120 random bits, so it cannot be guessed) and shows it once; the person
/// keeps it somewhere safe and needs it only to bring a backup back on a device that has nothing. It is separate from the password that signs
/// in to the server, and it never leaves the device, which is why the server cannot read a backup, and why a lost code cannot be recovered.
/// </summary>
public static class RecoveryCode
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // no I, O, 0 or 1, which are easy to mix up
    private const int Groups = 6, GroupLength = 4;

    /// <summary>A new code, like <c>K7QD-M3XF-9PLR-2WTE-HV8N-C4YB</c>.</summary>
    public static string Generate() =>
        Format(new string(Enumerable.Range(0, Groups * GroupLength).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray()));

    /// <summary>The code as it is kept, however it was typed (any case, with or without the dashes or spaces). Null if it is not a code.</summary>
    public static string? Normalize(string? typed)
    {
        var letters = new string((typed ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return letters.Length == Groups * GroupLength && letters.All(Alphabet.Contains) ? Format(letters) : null;
    }

    private static string Format(string letters) =>
        string.Join('-', Enumerable.Range(0, Groups).Select(g => letters.Substring(g * GroupLength, GroupLength)));
}

/// <summary>
/// Signs this device in to a backup server through the person's browser, so the app never sees their password. The app listens on a port of
/// this computer, opens the browser at the server's sign-in page, and the browser comes back to that port with a code that works once. The
/// code is traded for a token together with a secret only this app knows (PKCE), so a code that something else saw pass by is of no use.
/// </summary>
public sealed class BrowserSignIn : IDisposable
{
    private readonly Uri _server;
    private readonly TcpListener _listener;
    private readonly string _verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
    private readonly string _state = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    /// <summary>The page to open in the browser.</summary>
    public Uri LoginUrl { get; }

    /// <summary>Where the browser comes back to, on this computer.</summary>
    public Uri CallbackUrl { get; }

    public BrowserSignIn(Uri server, string deviceName)
    {
        _server = server;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        CallbackUrl = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/callback");
        var challenge = Challenge(_verifier);
        LoginUrl = new Uri(server, $"/login?redirect_uri={Uri.EscapeDataString(CallbackUrl.ToString())}&code_challenge={challenge}&state={_state}&device={Uri.EscapeDataString(deviceName)}");
    }

    internal static string Challenge(string verifier) => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public void Dispose() => _listener.Stop();

    /// <summary>
    /// Waits for the browser to come back, then trades the code for a token. Throws <see cref="OperationCanceledException"/> if cancelled and
    /// <see cref="BackupServerException"/> if the person takes too long or the server refuses.
    /// </summary>
    public async Task<(string Token, string Username)> WaitAsync(HttpClient? http = null, TimeSpan? timeout = null, CancellationToken cancel = default)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timer.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
        string? code = null;
        try
        {
            while (code == null)
            {
                using var browser = await _listener.AcceptTcpClientAsync(timer.Token);
                code = await AnswerAsync(browser, timer.Token);
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new BackupServerException("Nobody signed in within five minutes. Try again.");
        }
        finally { _listener.Stop(); }

        var owned = http == null;
        http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            using var response = await http.PostAsJsonAsync(new Uri(_server, "/v1/token"), new { code, verifier = _verifier }, cancel);
            if (!response.IsSuccessStatusCode) throw await BackupServerClient.FailureAsync(response, cancel);
            var answer = await response.Content.ReadFromJsonAsync<TokenJson>(BackupServerClient.Json, cancel);
            if (string.IsNullOrEmpty(answer?.Token)) throw new BackupServerException("The backup server did not finish the sign-in.");
            return (answer.Token, answer.Username ?? "");
        }
        catch (HttpRequestException ex) { throw new BackupServerException("Couldn't reach the backup server: " + ex.Message, inner: ex); }
        finally { if (owned) http.Dispose(); }
    }

    internal sealed record TokenJson(string? Token, string? Username);

    // Reads one request from the browser. The one that carries the code (for this sign-in: the state has to be ours) is answered with a page
    // that says it worked; anything else (a browser asks for an icon too) gets "not found" and the wait goes on.
    private async Task<string?> AnswerAsync(TcpClient browser, CancellationToken cancel)
    {
        var stream = browser.GetStream();
        var buffer = new byte[8192];
        int length = 0;
        while (length < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(length), cancel);
            if (n == 0) break;
            length += n;
            if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        var line = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n")[0].Split(' ');
        string? code = null;
        if (line.Length >= 2 && line[0] == "GET" && Uri.TryCreate(new Uri("http://127.0.0.1"), line[1], out var target) && target.AbsolutePath == "/callback")
        {
            var query = target.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2)).Where(p => p.Length == 2).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.First()[1]));
            if (query.TryGetValue("state", out var state) && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(state), Encoding.ASCII.GetBytes(_state))
                && query.TryGetValue("code", out var found) && found.Length is > 0 and < 200)
                code = found;
        }

        var page = code != null
            ? "<!doctype html><meta charset=utf-8><title>Signed in</title><body style=\"font:16px system-ui,sans-serif;margin:15vh auto;max-width:380px;padding:0 16px\"><h1 style=\"font-size:22px\">Signed in</h1><p>You can close this tab and go back to Yaparr.</p>"
            : "<!doctype html><meta charset=utf-8><title>Not found</title><p>Nothing here.";
        var body = Encoding.UTF8.GetBytes(page);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {(code != null ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, cancel);
        await stream.WriteAsync(body, cancel);
        await stream.FlushAsync(cancel);
        return code;
    }
}

/// <summary>
/// Keeps a locked profile backup on a backup server, so it can be fetched from any device. The person types the server's address and signs in
/// in their browser (<see cref="BrowserSignIn"/>); the app holds a token. What is sent is locked on this device with a recovery code
/// (<see cref="RecoveryCode"/>, <see cref="AppData.ExportProfile"/>) that never goes to the server, so the server stores something it cannot
/// read. How the server and the app talk is in <c>docs/backup-server.md</c>.
/// </summary>
public sealed class BackupServerClient : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _token;

    /// <summary>The address as it is used: https unless it is a local address, with no path, or null if it cannot be used.</summary>
    public static Uri? ParseServerAddress(string? typed)
    {
        var text = (typed ?? "").Trim();
        if (text.Length == 0) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return null;
        if (uri.Scheme == Uri.UriSchemeHttps) { }
        else if (uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))) { }
        else return null;   // a password is typed into this site and a token is sent to it: not over the internet in the clear
        return new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port).Uri;
    }

    /// <summary>
    /// Trades the one-time code in the button on the server's "You're in" page (a <c>yaparr://backup-signin/…</c> link) for a token. Unlike
    /// <see cref="BrowserSignIn"/> there is no secret of this app's own to prove it with: the code alone is the proof, which is why the person is asked to
    /// confirm before this is called, and why the server makes it work once, for five minutes, for the account that has just been set up.
    /// </summary>
    public static async Task<(string Token, string Username)> TradeHandoffCodeAsync(Uri server, string code, HttpClient? http = null, CancellationToken cancel = default)
    {
        var owned = http == null;
        http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            using var response = await http.PostAsJsonAsync(new Uri(server, "/v1/token/handoff"), new { code }, cancel);
            if (!response.IsSuccessStatusCode) throw await FailureAsync(response, cancel);
            var answer = await response.Content.ReadFromJsonAsync<BrowserSignIn.TokenJson>(Json, cancel);
            if (string.IsNullOrEmpty(answer?.Token)) throw new BackupServerException("The backup server did not finish the sign-in.");
            return (answer.Token, answer.Username ?? "");
        }
        catch (HttpRequestException ex) { throw new BackupServerException("Couldn't reach the backup server: " + ex.Message, inner: ex); }
        catch (TaskCanceledException ex) when (!cancel.IsCancellationRequested) { throw new BackupServerException("The backup server took too long to answer.", inner: ex); }
        finally { if (owned) http.Dispose(); }
    }

    /// <summary>The page where a person changes their password, for the browser.</summary>
    public static Uri PasswordPage(Uri server) => new(server, "/account/password");

    public BackupServerClient(Uri server, string token, HttpClient? http = null)
    {
        _token = token;
        _ownsHttp = http == null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // Not set again when it already is: a client that was given one that has been used (a test, or a caller that shares one) must not throw.
        if (_http.BaseAddress != server) _http.BaseAddress = server;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    private HttpRequestMessage Request(HttpMethod method, string path, int? basedOn = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
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
    internal static async Task<BackupServerException> FailureAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return new BackupServerException("This computer is no longer signed in to the backup server. Sign in again.", response.StatusCode);
        string? message = null;
        try { message = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel)).RootElement.GetProperty("error").GetString(); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
        return new BackupServerException(message ?? $"The backup server answered {(int)response.StatusCode} {response.ReasonPhrase}.", response.StatusCode);
    }

    private sealed record VersionJson(int Version, long Size, DateTimeOffset At);
    private sealed record VersionsJson(string? Username, List<VersionJson>? Versions);

    /// <summary>The saved copies of the backup, newest first (empty if there is none). Also what tells whether this device is still signed in.</summary>
    public async Task<IReadOnlyList<ServerBackupVersion>> VersionsAsync(CancellationToken cancel = default)
    {
        using var response = await SendAsync(Request(HttpMethod.Get, "/v1/backup/versions"), cancel);
        if (!response.IsSuccessStatusCode) throw await FailureAsync(response, cancel);
        var answer = await response.Content.ReadFromJsonAsync<VersionsJson>(Json, cancel);
        return (answer?.Versions ?? new()).Select(v => new ServerBackupVersion(v.Version, v.Size, v.At)).OrderByDescending(v => v.Version).ToList();
    }

    /// <summary>
    /// Saves a locked backup file as the newest version and returns its number. <paramref name="basedOn"/> is the newest version this device knew
    /// of (0 for none): if someone saved another since, it is refused with <see cref="BackupServerConflictException"/> instead of replacing it.
    /// </summary>
    public async Task<int> UploadAsync(string lockedBackupPath, int basedOn, CancellationToken cancel = default)
    {
        if (!ProfileCipher.IsLocked(lockedBackupPath)) throw new InvalidOperationException("Only a locked backup is sent to a server.");
        await using var content = new FileStream(lockedBackupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var request = Request(HttpMethod.Put, "/v1/backup", basedOn);
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

    /// <summary>Signs this device out on the server, so its token stops working. Quiet if the server already forgot it.</summary>
    public async Task SignOutAsync(CancellationToken cancel = default)
    {
        using var response = await SendAsync(Request(HttpMethod.Delete, "/v1/token"), cancel);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound) return;
        if (!response.IsSuccessStatusCode) throw await FailureAsync(response, cancel);
    }

    /// <summary>
    /// True if the code opens the backup file: the whole file is read and checked, and nothing is written. Used before a device adopts a code
    /// someone typed, so a slip cannot lead to backups locked with a code nobody has.
    /// </summary>
    public static bool CodeOpens(string lockedBackupPath, string recoveryCode)
    {
        try
        {
            using var input = new FileStream(lockedBackupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            ProfileCipher.Decrypt(input, Stream.Null, recoveryCode);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException) { return false; }
    }
}
