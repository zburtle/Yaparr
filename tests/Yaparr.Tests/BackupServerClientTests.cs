using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Yaparr.AppCore;
using Yaparr.Identity;

namespace Yaparr.Tests;

public sealed class BackupServerClientTests : IDisposable
{
    private const string Password = "correct horse battery";
    private static readonly Uri Server = new("https://backup.example.org");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-bsc-" + Guid.NewGuid().ToString("N"));

    public BackupServerClientTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>
    /// A backup server in memory. It checks signatures with its own code (not the client's), so a client that signs something the server would
    /// not accept fails here. It follows how a real one answers: 401 for an unknown name or a bad signature, 428 without If-Match, 409 when stale.
    /// </summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Keys = new();
        public readonly List<byte[]> Versions = new();
        public Func<DateTimeOffset> Now = () => DateTimeOffset.UtcNow;
        public bool Down;

        public void Add(string user, byte[] seed) => Keys[user] = new Ed25519PrivateKeyParameters(seed, 0).GeneratePublicKey().GetEncoded();

        private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            if (Down) throw new HttpRequestException("connection refused");
            string? H(string name) => request.Headers.TryGetValues(name, out var v) ? v.Single() : null;
            var path = request.RequestUri!.PathAndQuery;
            var body = request.Content == null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancel);

            var user = H("X-Yaparr-User");
            if (user == null || !Keys.TryGetValue(user, out var key)) return Json(HttpStatusCode.Unauthorized, new { error = "The username or password is wrong." });
            var time = long.Parse(H("X-Yaparr-Time")!);
            if (Math.Abs(Now().ToUnixTimeSeconds() - time) > 300) return Json(HttpStatusCode.Unauthorized, new { error = "The clock of this device is too far from the server's." });
            var hash = H("X-Yaparr-Content-Sha256")!;
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(key, 0));
            var text = Encoding.UTF8.GetBytes($"YAPSVC2\n{user}\n{request.Method.Method}\n{path}\n{time}\n{hash}");
            verifier.BlockUpdate(text, 0, text.Length);
            if (!verifier.VerifySignature(Base64Url.DecodeFromChars(H("X-Yaparr-Sig")!))) return Json(HttpStatusCode.Unauthorized, new { error = "The username or password is wrong." });
            if (Convert.ToHexStringLower(SHA256.HashData(body)) != hash) return Json(HttpStatusCode.BadRequest, new { error = "What arrived is not what was signed." });

            if (path == "/v1/backup/versions" && request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, Versions.Select((_, i) => new { version = i + 1, size = Versions[i].Length, at = DateTimeOffset.UtcNow }).Reverse());

            if (path == "/v1/backup" && request.Method == HttpMethod.Put)
            {
                if (request.Headers.IfMatch.Count == 0) return Json((HttpStatusCode)428, new { error = "Say which version this is based on." });
                var basedOn = int.Parse(request.Headers.IfMatch.Single().Tag.Trim('"'));
                if (basedOn != Versions.Count) return Json(HttpStatusCode.Conflict, new { error = "A newer backup exists.", version = Versions.Count });
                if (!body.AsSpan().StartsWith("YAPBAK1\n"u8)) return Json(HttpStatusCode.BadRequest, new { error = "Only a backup locked with a passphrase is accepted." });
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

            if (path == "/v1/account/key" && request.Method == HttpMethod.Put)
            {
                Keys[user] = Base64Url.DecodeFromChars(JsonDocument.Parse(body).RootElement.GetProperty("publicKey").GetString()!);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return Json(HttpStatusCode.NotFound, new { error = "Not found." });
        }
    }

    // A client with the small settings (quicker for tests than the real ones); how it signs is the real code.
    private static BackupServerClient Client(FakeServer server, string user = "sam", string password = Password) =>
        new(Server, user, BackupServerClient.SigningSeed(user, password, 8 * 1024, 1), new HttpClient(server), () => DateTimeOffset.UtcNow);

    private string LockedBackup(string name = "backup.yapbak")
    {
        var data = new AppData(Path.Combine(_dir, "profile"));
        using (var identity = PeerIdentity.Generate()) data.SaveIdentity(identity);
        data.SaveSettings(new AppSettings { DisplayName = "Zed" });
        var path = Path.Combine(_dir, name);
        data.ExportProfile(path, Password);
        return path;
    }

    private FakeServer ServerWith(string user = "sam", string password = Password)
    {
        var server = new FakeServer();
        server.Add(user, BackupServerClient.SigningSeed(user, password, 8 * 1024, 1));
        return server;
    }

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
    [InlineData("http://backup.example.org")]   // not over the internet in the clear
    [InlineData("ftp://backup.example.org")]
    [InlineData("https://")]
    public void An_address_that_cannot_be_used_is_refused(string? typed) => Assert.Null(BackupServerClient.ParseServerAddress(typed));

    [Theory]
    [InlineData("sam", "sam")]
    [InlineData("  Sam ", "sam")]
    [InlineData("a.b-c_d", "a.b-c_d")]
    [InlineData("ab", null)]
    [InlineData("-sam", null)]
    [InlineData("sam smith", null)]
    [InlineData("", null)]
    public void A_username_is_kept_as_the_server_keeps_it(string typed, string? expected) => Assert.Equal(expected, BackupServerClient.NormalizeUsername(typed));

    [Fact]
    public void The_same_name_and_password_make_the_same_key_and_nothing_else_does()
    {
        byte[] Seed(string user, string pw) => BackupServerClient.SigningSeed(user, pw, 8 * 1024, 1);
        Assert.Equal(Seed("sam", Password), Seed("  SAM", Password));
        Assert.NotEqual(Seed("sam", Password), Seed("alex", Password));
        Assert.NotEqual(Seed("sam", Password), Seed("sam", Password + "!"));
        Assert.Throws<ArgumentException>(() => Seed("sam", "short"));
        Assert.Throws<ArgumentException>(() => Seed("no spaces", Password));
    }

    [Fact]
    public void Signing_is_the_same_as_the_server_expects_down_to_the_byte()
    {
        // A fixed key, name and time make a fixed signature, which the code of the server checks too, so the two cannot drift apart unnoticed.
        var seed = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var headers = BackupServerClient.Sign(seed, "sam", "get", "/v1/backup/versions", 1_700_000_000,
            Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>())));
        Assert.Equal("sam", headers["X-Yaparr-User"]);
        Assert.Equal("1700000000", headers["X-Yaparr-Time"]);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", headers["X-Yaparr-Content-Sha256"]);
        Assert.Equal("aRyonQ8cTcTj5OsOnHVrdNM4o77gdFZsgazLs5vwzInhgp-_7pffD2G-XbHvg_jbZK0-YMxfPbgymje_4QopAA", headers["X-Yaparr-Sig"]);
    }

    [Fact]
    public void The_key_made_from_a_name_and_password_is_the_one_the_server_makes_for_an_account()
    {
        // The public key the server's own "accounts add sam --password ..." stores for this name and password, with the real settings. If this
        // fails, an account made on the server would not accept the login of the app.
        var seed = BackupServerClient.SigningSeed("sam", Password, 64 * 1024, 3);
        Assert.Equal("6Kn/wTntFsynrBg8OrBKkovzDHVWJXOG/H1c1JugIFA=", Convert.ToBase64String(BackupServerClient.PublicKeyOf(seed)));
    }

    [Fact]
    public async Task A_backup_is_saved_and_comes_back_and_restores_with_the_password()
    {
        var server = ServerWith();
        using var client = Client(server);
        Assert.Empty(await client.VersionsAsync());

        var file = LockedBackup();
        Assert.Equal(1, await client.UploadAsync(file, basedOn: 0));
        Assert.Equal(new byte[] { 1 }, (await client.VersionsAsync()).Select(v => (byte)v.Version).ToArray());
        Assert.Equal(File.ReadAllBytes(file), server.Versions[0]);   // what the server holds is the locked file, as it left

        var back = Path.Combine(_dir, "downloaded.yapbak");
        Assert.Equal(1, await client.DownloadAsync(back));
        var staging = new AppData(Path.Combine(_dir, "restored")).StageProfile(back, Password);
        try { Assert.True(File.Exists(Path.Combine(staging, "identity.key"))); }
        finally { Directory.Delete(staging, true); }
    }

    [Fact]
    public async Task A_save_over_a_newer_backup_is_refused_and_says_which_is_newest()
    {
        var server = ServerWith();
        using var client = Client(server);
        var file = LockedBackup();
        await client.UploadAsync(file, 0);
        await client.UploadAsync(file, 1);
        var conflict = await Assert.ThrowsAsync<BackupServerConflictException>(() => client.UploadAsync(file, 1));
        Assert.Equal(2, conflict.NewestVersion);
        Assert.Equal(2, server.Versions.Count);
    }

    [Fact]
    public async Task A_plain_backup_is_never_sent()
    {
        var server = ServerWith();
        using var client = Client(server);
        var plain = Path.Combine(_dir, "plain.zip");
        new AppData(Path.Combine(_dir, "profile2")).Also(d => { using var id = PeerIdentity.Generate(); d.SaveIdentity(id); d.SaveSettings(new AppSettings()); d.ExportProfile(plain); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.UploadAsync(plain, 0));
        Assert.Empty(server.Versions);
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_name_say_the_same_thing()
    {
        var server = ServerWith();
        using var wrong = Client(server, "sam", "not the right password");
        using var stranger = Client(server, "alex");
        var a = await Assert.ThrowsAsync<BackupServerException>(() => wrong.VersionsAsync());
        var b = await Assert.ThrowsAsync<BackupServerException>(() => stranger.VersionsAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, a.Status);
        Assert.Equal(a.Message, b.Message);
        Assert.Contains("username or password", a.Message);
    }

    [Fact]
    public async Task A_clock_that_is_far_off_is_said_so()
    {
        var server = ServerWith();
        server.Now = () => DateTimeOffset.UtcNow.AddHours(3);
        using var client = Client(server);
        var ex = await Assert.ThrowsAsync<BackupServerException>(() => client.VersionsAsync());
        Assert.Contains("clock", ex.Message);
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_is_reported_plainly()
    {
        var server = ServerWith();
        server.Down = true;
        using var client = Client(server);
        var ex = await Assert.ThrowsAsync<BackupServerException>(() => client.VersionsAsync());
        Assert.StartsWith("Couldn't reach the backup server", ex.Message);
    }

    [Fact]
    public async Task Fetching_when_there_is_no_backup_says_so_and_leaves_no_file()
    {
        using var client = Client(ServerWith());
        var path = Path.Combine(_dir, "none.yapbak");
        var ex = await Assert.ThrowsAsync<BackupServerException>(() => client.DownloadAsync(path));
        Assert.Contains("no backup", ex.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Changing_the_password_replaces_the_key_on_the_server()
    {
        var server = ServerWith();
        using (var client = Client(server)) await client.UploadAsync(LockedBackup(), 0);

        var newKey = BackupServerClient.SigningSeed("sam", "a brand new password", 8 * 1024, 1);
        // The real client makes the new key with the real (slow) settings; the fake accepts any key, so this checks the request and that it is signed.
        using (var client = Client(server)) await client.ChangePasswordAsync("a brand new password");
        Assert.NotEqual(BackupServerClient.PublicKeyOf(BackupServerClient.SigningSeed("sam", Password, 8 * 1024, 1)), server.Keys["sam"]);
        Assert.Equal(32, server.Keys["sam"].Length);
        _ = newKey;
    }

    [Fact]
    public async Task Deleting_the_backup_removes_it_and_a_second_delete_is_quiet()
    {
        var server = ServerWith();
        using var client = Client(server);
        await client.UploadAsync(LockedBackup(), 0);
        await client.DeleteAsync();
        Assert.Empty(server.Versions);
        await client.DeleteAsync();
    }

    [Fact]
    public void The_form_checks_each_field_and_says_which_is_wrong()
    {
        var good = Yaparr.Desktop.BackupServerForm.Read(" backup.example.org ", " Sam ", Password, out var none);
        Assert.NotNull(good);
        Assert.Equal("", none);
        Assert.Equal("https://backup.example.org/", good!.Server.ToString());
        Assert.Equal("sam", good.Username);
        Assert.Equal(Password, good.Password);

        Assert.Null(Yaparr.Desktop.BackupServerForm.Read("", "sam", Password, out var address));
        Assert.Contains("address", address);
        Assert.Null(Yaparr.Desktop.BackupServerForm.Read("http://backup.example.org", "sam", Password, out address));
        Assert.Contains("https://", address);
        Assert.Null(Yaparr.Desktop.BackupServerForm.Read("backup.example.org", "no spaces", Password, out var user));
        Assert.Contains("username", user);
        Assert.Null(Yaparr.Desktop.BackupServerForm.Read("backup.example.org", "sam", "short", out var password));
        Assert.Contains("password", password);
    }

    [Fact]
    public void A_backup_server_setting_survives_a_save_and_load()
    {
        var data = new AppData(Path.Combine(_dir, "settings"));
        data.SaveSettings(new AppSettings { BackupServerUrl = "https://backup.example.org", BackupServerUser = "sam", BackupServerVersion = 4 });
        var loaded = data.LoadSettings();
        Assert.Equal("https://backup.example.org", loaded.BackupServerUrl);
        Assert.Equal("sam", loaded.BackupServerUser);
        Assert.Equal(4, loaded.BackupServerVersion);
        Assert.DoesNotContain("password", File.ReadAllText(Path.Combine(_dir, "settings", "settings.json")), StringComparison.OrdinalIgnoreCase);
    }
}

internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
