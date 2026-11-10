using System.Net;
using Yaparr.AppCore;
using Yaparr.Identity;

namespace Yaparr.Tests;

/// <summary>
/// The whole path against a real backup server: sign in as a browser would, save a backup, fetch it, restore it, sign out. It runs only when
/// told where a server is, since there is none in this repository:
/// <code>
/// YAPARR_TEST_BACKUP_SERVER=http://127.0.0.1:8080  YAPARR_TEST_BACKUP_USER=sam  YAPARR_TEST_BACKUP_PASSWORD=...  dotnet test --filter BackupServerLive
/// </code>
/// Without those it does nothing and passes.
/// </summary>
public sealed class BackupServerLiveTests
{
    [Fact]
    public async Task Sign_in_back_up_restore_and_sign_out_against_a_real_server()
    {
        var address = Environment.GetEnvironmentVariable("YAPARR_TEST_BACKUP_SERVER");
        var user = Environment.GetEnvironmentVariable("YAPARR_TEST_BACKUP_USER");
        var password = Environment.GetEnvironmentVariable("YAPARR_TEST_BACKUP_PASSWORD");
        if (string.IsNullOrEmpty(address) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password)) return;

        var server = BackupServerClient.ParseServerAddress(address) ?? throw new InvalidOperationException("YAPARR_TEST_BACKUP_SERVER is not a usable address.");
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Sign in: the app waits, and this plays the browser (the page, the form, and the trip back to the app).
            string token;
            using (var signIn = new BrowserSignIn(server, "The live test"))
            {
                var waiting = signIn.WaitAsync();
                using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                var page = await browser.GetAsync(signIn.LoginUrl);
                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                Assert.Contains("The live test", await page.Content.ReadAsStringAsync());

                var asked = signIn.LoginUrl.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                var wrong = await browser.PostAsync(new Uri(server, "/login"), new FormUrlEncodedContent(new Dictionary<string, string>(asked) { ["username"] = user, ["password"] = password + "-wrong" }));
                Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

                var signedIn = await browser.PostAsync(new Uri(server, "/login"), new FormUrlEncodedContent(new Dictionary<string, string>(asked) { ["username"] = user, ["password"] = password }));
                Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
                Assert.StartsWith(signIn.CallbackUrl.ToString(), signedIn.Headers.Location!.ToString());
                Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(signedIn.Headers.Location)).StatusCode);

                (token, var username) = await waiting;
                Assert.Equal(user.Trim().ToLowerInvariant(), username);
            }

            // Back up: a profile, locked with a recovery code, saved on top of whatever the account has.
            using var client = new BackupServerClient(server, token);
            var before = await client.VersionsAsync();
            var newest = before.Count == 0 ? 0 : before[0].Version;

            var data = new AppData(Path.Combine(dir, "profile"));
            using var identity = PeerIdentity.Generate();
            data.SaveIdentity(identity);
            data.SaveSettings(new AppSettings { DisplayName = "Live" });
            var code = RecoveryCode.Generate();
            var file = Path.Combine(dir, "backup.yapbak");
            data.ExportProfile(file, code);
            var version = await client.UploadAsync(file, newest);
            Assert.Equal(newest + 1, version);
            await Assert.ThrowsAsync<BackupServerConflictException>(() => client.UploadAsync(file, newest));   // the same save sent twice

            // Restore: fetched, opened with the code only, and it is the same account as another device.
            var back = Path.Combine(dir, "downloaded.yapbak");
            Assert.Equal(version, await client.DownloadAsync(back));
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(back));
            Assert.False(BackupServerClient.CodeOpens(back, RecoveryCode.Generate()));
            var staging = new AppData(Path.Combine(dir, "restored")).StageProfile(back, code);
            using (var restored = PeerIdentity.Import(File.ReadAllBytes(Path.Combine(staging, "identity.key"))))
            {
                Assert.Equal(identity.AccountId, restored.AccountId);
                Assert.NotEqual(identity.Id, restored.Id);
            }

            // Sign out: the token stops working.
            await client.SignOutAsync();
            Assert.True((await Assert.ThrowsAsync<BackupServerException>(() => client.VersionsAsync())).SignedOut);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
