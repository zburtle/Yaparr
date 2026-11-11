using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yaparr.AppCore;
using Yaparr.Identity;

namespace Yaparr.Tests;

public sealed class BackupServerClientTests : IDisposable
{
    private static readonly Uri Server = new("https://backup.example.org");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-bsc-" + Guid.NewGuid().ToString("N"));

    public BackupServerClientTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>
    /// A backup server in memory, answering as a real one does: 401 without a token it gave out, 428 without If-Match, 409 when stale, and a
    /// sign-in code that is traded for a token only with the verifier its challenge was made from.
    /// </summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public readonly Dictionary<string, string> Tokens = new();                          // token -> username
        public readonly Dictionary<string, (string User, string Challenge)> Codes = new();
        public readonly List<byte[]> Versions = new();
        public bool Down;

        public string SignIn(string user)
        {
            var token = "ycl_" + Guid.NewGuid().ToString("N");
            Tokens[token] = user;
            return token;
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            if (Down) throw new HttpRequestException("connection refused");
            var path = request.RequestUri!.PathAndQuery;
            var body = request.Content == null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancel);

            if (path == "/v1/token" && request.Method == HttpMethod.Post)
            {
                var asked = JsonDocument.Parse(body).RootElement;
                var code = asked.GetProperty("code").GetString()!;
                var verifier = asked.GetProperty("verifier").GetString()!;
                if (!Codes.Remove(code, out var pending) || Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) != pending.Challenge)
                    return Json(HttpStatusCode.BadRequest, new { error = "That sign-in has expired or was already used. Sign in again." });
                return Json(HttpStatusCode.OK, new { token = SignIn(pending.User), username = pending.User });
            }

            var token = request.Headers.Authorization is { Scheme: "Bearer" } auth ? auth.Parameter : null;
            if (token == null || !Tokens.TryGetValue(token, out var user)) return Json(HttpStatusCode.Unauthorized, new { error = "This device is not signed in. Sign in again." });

            if (path == "/v1/token" && request.Method == HttpMethod.Delete)
            {
                Tokens.Remove(token);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path == "/v1/backup/versions" && request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, new { username = user, versions = Versions.Select((v, i) => new { version = i + 1, size = v.Length, at = DateTimeOffset.UtcNow }).Reverse() });

            if (path == "/v1/backup" && request.Method == HttpMethod.Put)
            {
                if (request.Headers.IfMatch.Count == 0) return Json((HttpStatusCode)428, new { error = "Say which version this is based on." });
                var basedOn = int.Parse(request.Headers.IfMatch.Single().Tag.Trim('"'));
                if (basedOn != Versions.Count) return Json(HttpStatusCode.Conflict, new { error = "A newer backup exists.", version = Versions.Count });
                if (!body.AsSpan().StartsWith("YAPBAK1\n"u8)) return Json(HttpStatusCode.BadRequest, new { error = "Only a locked backup is accepted." });
                Versions.Add(body);
                return Json(HttpStatusCode.Created, new { version = Versions.Count, size = body.Length });
            }

            if (path.StartsWith("/v1/backup") && request.Method == HttpMethod.Get)
            {
                if (Versions.Count == 0) return Json(HttpStatusCode.NotFound, new { error = "There is no backup." });
                var version = path.Contains("version=") ? int.Parse(path[(path.IndexOf("version=") + 8)..]) : Versions.Count;
                if (version < 1 || version > Versions.Count) return Json(HttpStatusCode.NotFound, new { error = "There is no backup." });
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Versions[version - 1]) };
                response.Headers.Add("X-Yaparr-Version", version.ToString());
                return response;
            }

            if (path == "/v1/backup" && request.Method == HttpMethod.Delete)
            {
                if (Versions.Count == 0) return Json(HttpStatusCode.NotFound, new { error = "There is no backup." });
                Versions.Clear();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return Json(HttpStatusCode.NotFound, new { error = "Not found." });
        }
    }

    private static BackupServerClient Client(FakeServer server, string? token = null) => new(Server, token ?? server.SignIn("sam"), new HttpClient(server));

    private string LockedBackup(string code, string name = "backup.yapbak")
    {
        var data = new AppData(Path.Combine(_dir, "profile"));
        if (!data.HasIdentity) { using var identity = PeerIdentity.Generate(); data.SaveIdentity(identity); }
        data.SaveSettings(new AppSettings { DisplayName = "Zed" });
        var path = Path.Combine(_dir, name);
        data.ExportProfile(path, code);
        return path;
    }

    // ---- the address and the recovery code ----

    [Theory]
    [InlineData("backup.example.org", "https://backup.example.org/")]
    [InlineData("  https://backup.example.org  ", "https://backup.example.org/")]
    [InlineData("https://backup.example.org:8443/some/path?x=1", "https://backup.example.org:8443/")]
    [InlineData("http://localhost:5000", "http://localhost:5000/")]
    [InlineData("http://127.0.0.1:5000/", "http://127.0.0.1:5000/")]
    public void A_server_address_is_tidied_to_its_origin(string typed, string expected) =>
        Assert.Equal(expected, BackupServerClient.ParseServerAddress(typed)?.ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://backup.example.org")]   // a password is typed into it: not over the internet in the clear
    [InlineData("ftp://backup.example.org")]
    [InlineData("https://")]
    public void An_address_that_cannot_be_used_is_refused(string? typed) => Assert.Null(BackupServerClient.ParseServerAddress(typed));

    [Fact]
    public void A_recovery_code_is_long_random_and_easy_to_read_out()
    {
        var code = RecoveryCode.Generate();
        Assert.Matches("^[A-HJ-NP-Z2-9]{4}(-[A-HJ-NP-Z2-9]{4}){5}$", code);   // six groups of four, without I, O, 0 or 1
        Assert.NotEqual(code, RecoveryCode.Generate());
        Assert.Null(ProfileCipher.CheckPassphrase(code));                      // long enough to lock a backup
        Assert.Equal(200, Enumerable.Range(0, 200).Select(_ => RecoveryCode.Generate()).Distinct().Count());
    }

    [Theory]
    [InlineData("K7QD-M3XF-9PLR-2WTE-HV8N-C4YB")]
    [InlineData("k7qd-m3xf-9plr-2wte-hv8n-c4yb")]
    [InlineData("K7QDM3XF9PLR2WTEHV8NC4YB")]
    [InlineData("  k7qd m3xf 9plr 2wte hv8n c4yb  ")]
    public void A_recovery_code_is_understood_however_it_is_typed(string typed) =>
        Assert.Equal("K7QD-M3XF-9PLR-2WTE-HV8N-C4YB", RecoveryCode.Normalize(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("K7QD-M3XF-9PLR-2WTE-HV8N")]            // a group short
    [InlineData("K7QD-M3XF-9PLR-2WTE-HV8N-C4YB-AAAA")]  // a group too many
    [InlineData("K7QD-M3XF-9PLR-2WTE-HV8N-C4Y0")]       // a zero is not in a code
    [InlineData("correct horse battery staple")]
    public void Something_that_is_not_a_recovery_code_is_refused(string? typed) => Assert.Null(RecoveryCode.Normalize(typed));

    [Fact]
    public void What_this_device_remembers_is_kept_apart_from_the_settings_and_out_of_a_backup()
    {
        var data = new AppData(Path.Combine(_dir, "state"));
        Assert.False(data.LoadBackupServer().SignedIn);
        data.SaveBackupServer(new BackupServerState { Url = "https://backup.example.org", Username = "sam", Token = "ycl_secret-token", RecoveryCode = "K7QD-M3XF-9PLR-2WTE-HV8N-C4YB", Version = 4 });

        var loaded = data.LoadBackupServer();
        Assert.True(loaded.SignedIn);
        Assert.Equal("sam", loaded.Username);
        Assert.Equal("ycl_secret-token", loaded.Token);
        Assert.Equal("K7QD-M3XF-9PLR-2WTE-HV8N-C4YB", loaded.RecoveryCode);
        Assert.Equal(4, loaded.Version);

        // A backup (even a plain one) does not carry the token or the code that locks it.
        using (var identity = PeerIdentity.Generate()) data.SaveIdentity(identity);
        data.SaveSettings(new AppSettings());
        var zip = Path.Combine(_dir, "plain.zip");
        data.ExportProfile(zip);
        using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
        Assert.DoesNotContain(archive.Entries, e => e.FullName.Contains("backup-server", StringComparison.OrdinalIgnoreCase));
        Assert.False(AppData.IsProfilePath("backup-server.json"));
    }

    // ---- signing in through the browser ----

    [Fact]
    public void The_challenge_is_the_standard_one()
    {
        // The example from the standard (RFC 7636, appendix B), so any server that follows it accepts what the app sends.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", BrowserSignIn.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    private static Dictionary<string, string> QueryOf(Uri uri) =>
        uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    [Fact]
    public async Task Signing_in_opens_the_servers_page_and_trades_the_code_the_browser_brings_back()
    {
        var server = new FakeServer();
        using var signIn = new BrowserSignIn(Server, "Sam's laptop");

        // What the browser is sent to.
        Assert.Equal("backup.example.org", signIn.LoginUrl.Host);
        Assert.Equal("/login", signIn.LoginUrl.AbsolutePath);
        var asked = QueryOf(signIn.LoginUrl);
        Assert.Equal(signIn.CallbackUrl.ToString(), asked["redirect_uri"]);
        Assert.StartsWith("http://127.0.0.1:", asked["redirect_uri"]);
        Assert.Equal("Sam's laptop", asked["device"]);
        Assert.Equal(43, asked["code_challenge"].Length);

        var waiting = signIn.WaitAsync(new HttpClient(server));

        // The person signs in on the server's page, which sends the browser back to the app with a code.
        server.Codes["the-code"] = ("sam", asked["code_challenge"]);
        using var browser = new HttpClient();
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(new Uri(signIn.CallbackUrl, "/favicon.ico"))).StatusCode);            // a browser asks for this too
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(signIn.CallbackUrl + "?code=the-code&state=someone-elses")).StatusCode); // not this sign-in
        Assert.False(waiting.IsCompleted);
        var page = await browser.GetAsync(signIn.CallbackUrl + "?code=the-code&state=" + asked["state"]);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("go back to Yaparr", await page.Content.ReadAsStringAsync());

        var (token, username) = await waiting;
        Assert.Equal("sam", username);
        Assert.Equal("sam", server.Tokens[token]);

        // And the token works.
        using var client = new BackupServerClient(Server, token, new HttpClient(server));
        Assert.Empty(await client.VersionsAsync());
    }

    [Fact]
    public async Task A_code_the_server_refuses_fails_the_sign_in_with_the_servers_words()
    {
        var server = new FakeServer();
        using var signIn = new BrowserSignIn(Server, "x");
        var waiting = signIn.WaitAsync(new HttpClient(server));
        using var browser = new HttpClient();
        await browser.GetAsync(signIn.CallbackUrl + "?code=never-issued&state=" + QueryOf(signIn.LoginUrl)["state"]);
        var ex = await Assert.ThrowsAsync<BackupServerException>(() => waiting);
        Assert.Contains("Sign in again", ex.Message);
    }

    [Fact]
    public async Task Waiting_for_the_browser_can_be_cancelled_and_gives_up_after_a_while()
    {
        using (var signIn = new BrowserSignIn(Server, "x"))
        {
            using var cancel = new CancellationTokenSource();
            var waiting = signIn.WaitAsync(new HttpClient(new FakeServer()), cancel: cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        using (var signIn = new BrowserSignIn(Server, "x"))
        {
            var ex = await Assert.ThrowsAsync<BackupServerException>(() => signIn.WaitAsync(new HttpClient(new FakeServer()), timeout: TimeSpan.FromMilliseconds(100)));
            Assert.Contains("Nobody signed in", ex.Message);
        }
    }

    // ---- backups ----

    [Fact]
    public async Task A_backup_is_saved_and_comes_back_and_restores_with_the_recovery_code()
    {
        var server = new FakeServer();
        using var client = Client(server);
        Assert.Empty(await client.VersionsAsync());

        var code = RecoveryCode.Generate();
        var file = LockedBackup(code);
        Assert.Equal(1, await client.UploadAsync(file, basedOn: 0));
        Assert.Equal(new[] { 1 }, (await client.VersionsAsync()).Select(v => v.Version).ToArray());
        Assert.Equal(File.ReadAllBytes(file), server.Versions[0]);                                     // what the server holds is the locked file, as it left
        Assert.DoesNotContain(code, Encoding.ASCII.GetString(server.Versions[0]));                    // and the code is nowhere in it

        var back = Path.Combine(_dir, "downloaded.yapbak");
        Assert.Equal(1, await client.DownloadAsync(back));
        Assert.True(BackupServerClient.CodeOpens(back, code));
        Assert.False(BackupServerClient.CodeOpens(back, RecoveryCode.Generate()));                    // another code does not open it
        var staging = new AppData(Path.Combine(_dir, "restored")).StageProfile(back, code);
        try { Assert.True(File.Exists(Path.Combine(staging, "identity.key"))); }
        finally { Directory.Delete(staging, true); }
    }

    [Fact]
    public async Task A_save_over_a_newer_backup_is_refused_and_says_which_is_newest()
    {
        var server = new FakeServer();
        using var client = Client(server);
        var file = LockedBackup(RecoveryCode.Generate());
        await client.UploadAsync(file, 0);
        await client.UploadAsync(file, 1);
        var conflict = await Assert.ThrowsAsync<BackupServerConflictException>(() => client.UploadAsync(file, 1));
        Assert.Equal(2, conflict.NewestVersion);
        Assert.Equal(2, server.Versions.Count);
    }

    [Fact]
    public async Task A_plain_backup_is_never_sent()
    {
        var server = new FakeServer();
        using var client = Client(server);
        var data = new AppData(Path.Combine(_dir, "profile2"));
        using (var identity = PeerIdentity.Generate()) data.SaveIdentity(identity);
        data.SaveSettings(new AppSettings());
        var plain = Path.Combine(_dir, "plain.zip");
        data.ExportProfile(plain);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.UploadAsync(plain, 0));
        Assert.Empty(server.Versions);
    }

    [Fact]
    public async Task A_device_the_server_no_longer_knows_is_told_to_sign_in_again()
    {
        var server = new FakeServer();
        using var stranger = Client(server, "ycl_never-given-out");
        var ex = await Assert.ThrowsAsync<BackupServerException>(() => stranger.VersionsAsync());
        Assert.True(ex.SignedOut);
        Assert.Contains("Sign in again", ex.Message);

        // Signing out makes the token stop working, and signing out twice is quiet.
        var token = server.SignIn("sam");
        using var client = Client(server, token);
        await client.SignOutAsync();
        Assert.DoesNotContain(token, server.Tokens.Keys);
        Assert.True((await Assert.ThrowsAsync<BackupServerException>(() => client.VersionsAsync())).SignedOut);
        await client.SignOutAsync();
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_is_reported_plainly()
    {
        var server = new FakeServer { Down = true };
        using var client = Client(server, "ycl_x");
        var ex = await Assert.ThrowsAsync<BackupServerException>(() => client.VersionsAsync());
        Assert.StartsWith("Couldn't reach the backup server", ex.Message);
        Assert.False(ex.SignedOut);
    }

    [Fact]
    public async Task Fetching_when_there_is_no_backup_says_so_and_leaves_no_file()
    {
        using var client = Client(new FakeServer());
        var path = Path.Combine(_dir, "none.yapbak");
        var ex = await Assert.ThrowsAsync<BackupServerException>(() => client.DownloadAsync(path));
        Assert.Contains("no backup", ex.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Deleting_the_backup_removes_it_and_a_second_delete_is_quiet()
    {
        var server = new FakeServer();
        using var client = Client(server);
        await client.UploadAsync(LockedBackup(RecoveryCode.Generate()), 0);
        await client.DeleteAsync();
        Assert.Empty(server.Versions);
        await client.DeleteAsync();
    }

    [Fact]
    public void The_password_page_is_on_the_server() =>
        Assert.Equal("https://backup.example.org/account/password", BackupServerClient.PasswordPage(Server).ToString());
}
