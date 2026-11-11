using Yaparr.AppCore;

namespace Yaparr.Tests;

public sealed class ReleaseConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-release-" + Guid.NewGuid().ToString("N"));

    public ReleaseConfigTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private ReleaseConfig LoadWith(string? json)
    {
        if (json != null) File.WriteAllText(Path.Combine(_dir, ReleaseConfig.FileName), json);
        return ReleaseConfig.Load(_dir);
    }

    [Fact]
    public void The_backup_server_a_release_ships_with_is_read()
    {
        Assert.Equal("https://cloud.yaparr.com/", LoadWith("""{ "backupServer": "https://cloud.yaparr.com" }""").BackupServer?.ToString());
        Assert.Equal("https://cloud.yaparr.com/", LoadWith("""{ "backupServer": "  cloud.yaparr.com/some/path " }""").BackupServer?.ToString());   // tidied like a typed address
        Assert.Equal("https://cloud.yaparr.com/", LoadWith("""{ "BackupServer": "https://cloud.yaparr.com" }""").BackupServer?.ToString());
        Assert.Equal("https://cloud.yaparr.com/", LoadWith("// the server\n{ \"backupServer\": \"https://cloud.yaparr.com\", }").BackupServer?.ToString());   // comments and a trailing comma are fine
    }

    [Theory]
    [InlineData(null)]                                          // no file at all
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "backupServer": "" }""")]
    [InlineData("""{ "backupServer": "http://cloud.yaparr.com" }""")]   // not over the internet in the clear
    [InlineData("""{ "backupServer": 5 }""")]
    public void A_missing_or_unusable_file_means_no_default_and_never_stops_the_app(string? json) => Assert.Null(LoadWith(json).BackupServer);

    [Fact]
    public void A_huge_file_is_not_read()
    {
        File.WriteAllText(Path.Combine(_dir, ReleaseConfig.FileName), """{ "backupServer": "https://cloud.yaparr.com", "x": """ + "\"" + new string('a', 100_000) + "\" }");
        Assert.Null(ReleaseConfig.Load(_dir).BackupServer);
    }

    [Fact]
    public void A_person_s_own_server_is_offered_before_the_releases()
    {
        var config = LoadWith("""{ "backupServer": "https://cloud.yaparr.com" }""");
        Assert.Equal("https://cloud.yaparr.com", config.BackupServerToOffer(null));
        Assert.Equal("https://cloud.yaparr.com", config.BackupServerToOffer("   "));
        Assert.Equal("https://friends.example.org", config.BackupServerToOffer(" https://friends.example.org "));
        Assert.Equal("", ReleaseConfig.None.BackupServerToOffer(null));
        Assert.Equal("https://friends.example.org", ReleaseConfig.None.BackupServerToOffer("https://friends.example.org"));
    }

    [Fact]
    public void The_file_that_ships_with_the_app_names_the_yaparr_backup_server()
    {
        // The desktop project copies it beside the program (and into the published folder the installer, the Flatpak and the .deb are made from),
        // so it is next to the test program too.
        var shipped = Path.Combine(AppContext.BaseDirectory, ReleaseConfig.FileName);
        Assert.True(File.Exists(shipped), "yaparr.release.json is copied to the output");
        Assert.Equal("https://cloud.yaparr.com/", ReleaseConfig.Load(AppContext.BaseDirectory).BackupServer?.ToString());
        Assert.Equal("https://cloud.yaparr.com/", ReleaseConfig.Current.BackupServer?.ToString());
    }
}
