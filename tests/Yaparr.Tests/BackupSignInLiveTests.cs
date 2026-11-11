using Yaparr.AppCore;
using Yaparr.Desktop;

namespace Yaparr.Tests;

/// <summary>
/// The button on a backup server's "You're in" page, against a real server: the link it carries is read as the app would read it, its one-time
/// code is traded for a token, the token works, and the code does not work a second time. It needs the link, which a person gets only by signing up,
/// so it runs only when told where one is, and does nothing otherwise:
/// <code>
/// YAPARR_TEST_BACKUP_SIGNIN_LINK="yaparr://backup-signin/…"  dotnet test --filter BackupSignInLive
/// </code>
/// </summary>
public sealed class BackupSignInLiveTests
{
    [Fact]
    public async Task The_link_on_the_youre_in_page_signs_the_app_in_once()
    {
        var text = Environment.GetEnvironmentVariable("YAPARR_TEST_BACKUP_SIGNIN_LINK");
        if (string.IsNullOrEmpty(text)) return;

        var link = DeepLink.Parse(text) ?? throw new InvalidOperationException("The link is not one the app understands.");
        Assert.True(link.TryGetBackupSignIn(out var code, out var server));

        var (token, username) = await BackupServerClient.TradeHandoffCodeAsync(server, code);
        Assert.False(string.IsNullOrEmpty(username));

        using (var client = new BackupServerClient(server, token))
            Assert.Empty(await client.VersionsAsync());                    // signed in, as a brand-new account with no backup

        // The code was spent by the first trade.
        var again = await Assert.ThrowsAsync<BackupServerException>(() => BackupServerClient.TradeHandoffCodeAsync(server, code));
        Assert.Contains("expired or was already used", again.Message);

        using (var client = new BackupServerClient(server, token))
        {
            await client.SignOutAsync();
            Assert.True((await Assert.ThrowsAsync<BackupServerException>(() => client.VersionsAsync())).SignedOut);
        }
    }
}
