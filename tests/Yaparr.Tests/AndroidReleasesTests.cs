using Xunit;
using Yaparr.Updates;

namespace Yaparr.Tests;

public class AndroidReleasesTests
{
    private const string Repo = "zburtle/Yaparr";
    private static readonly string Hash = new('a', 64);

    private static string Release(string tag, string? sha = null, string? url = null, string name = "Yaparr.apk", bool draft = false, bool prerelease = false) => $$"""
        {"tag_name":"{{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},"body":"notes",
         "assets":[{"name":"{{name}}","size":1234,"digest":"{{(sha == null ? "sha256:" + Hash : sha)}}","browser_download_url":"{{url ?? $"https://github.com/{Repo}/releases/download/{tag}/{name}"}}"}]}
        """;

    private static string List(params string[] releases) => "[" + string.Join(",", releases) + "]";

    [Fact]
    public void Picks_the_newest_android_release_and_ignores_the_computer_ones()
    {
        var json = List(Release("v1.59.2", name: "Yaparr.msi"), Release("android-v1.58.1"), Release("android-v1.58.0"), Release("v1.59.1"));
        var update = AndroidReleases.Newest(json, new Version(1, 57, 0), Repo);
        Assert.NotNull(update);
        Assert.Equal(new Version(1, 58, 1), update!.Version);
        Assert.Equal("android-v1.58.1", update.Tag);
        Assert.Equal(Hash, update.Sha256);
        Assert.Equal(1234, update.Size);
    }

    [Fact]
    public void Offers_nothing_when_the_phone_is_up_to_date()
    {
        var json = List(Release("android-v1.58.1"));
        Assert.Null(AndroidReleases.Newest(json, new Version(1, 58, 1), Repo));
        Assert.Null(AndroidReleases.Newest(json, new Version(1, 60, 0), Repo));
    }

    [Fact]
    public void Compares_versions_by_number_not_by_text()
    {
        var json = List(Release("android-v1.9.0"), Release("android-v1.10.0"));
        Assert.Equal(new Version(1, 10, 0), AndroidReleases.Newest(json, new Version(1, 8, 0), Repo)!.Version);
    }

    [Fact]
    public void Does_not_offer_drafts_or_pre_releases()
    {
        var json = List(Release("android-v1.60.0", draft: true), Release("android-v1.59.0", prerelease: true));
        Assert.Null(AndroidReleases.Newest(json, new Version(1, 0, 0), Repo));
    }

    [Fact]
    public void Does_not_offer_a_package_without_a_checksum()
    {
        var json = $$"""[{"tag_name":"android-v1.60.0","assets":[{"name":"Yaparr.apk","browser_download_url":"https://github.com/{{Repo}}/releases/download/android-v1.60.0/Yaparr.apk"}]}]""";
        Assert.Null(AndroidReleases.Newest(json, new Version(1, 0, 0), Repo));
        Assert.Null(AndroidReleases.Newest(List(Release("android-v1.60.0", sha: "sha256:nothex")), new Version(1, 0, 0), Repo));
    }

    [Fact]
    public void Does_not_offer_a_package_from_anywhere_else()
    {
        Assert.Null(AndroidReleases.Newest(List(Release("android-v1.60.0", url: "https://example.com/Yaparr.apk")), new Version(1, 0, 0), Repo));
        Assert.Null(AndroidReleases.Newest(List(Release("android-v1.60.0", url: "https://github.com/someone/else/releases/download/x/Yaparr.apk")), new Version(1, 0, 0), Repo));
        Assert.Null(AndroidReleases.Newest(List(Release("android-v1.60.0", url: "http://github.com/" + Repo + "/releases/download/x/Yaparr.apk")), new Version(1, 0, 0), Repo));
    }

    [Fact]
    public void Only_a_package_file_counts()
    {
        Assert.Null(AndroidReleases.Newest(List(Release("android-v1.60.0", name: "Yaparr.zip")), new Version(1, 0, 0), Repo));
    }

    [Fact]
    public void Survives_nonsense()
    {
        Assert.Null(AndroidReleases.Newest("{}", new Version(1, 0, 0), Repo));
        Assert.Null(AndroidReleases.Newest("[1,\"x\",null,{}]", new Version(1, 0, 0), Repo));
        Assert.Null(AndroidReleases.Newest("[{\"tag_name\":\"android-vbanana\"}]", new Version(1, 0, 0), Repo));
    }

    [Theory]
    [InlineData("1.58.1", 1, 58, 1)]
    [InlineData("1.58", 1, 58, 0)]
    [InlineData("1.58.1-beta.2", 1, 58, 1)]
    public void Reads_the_versions_the_app_reports(string text, int major, int minor, int patch)
    {
        Assert.True(AndroidReleases.TryParseVersion(text, out var version));
        Assert.Equal(new Version(major, minor, patch), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("banana")]
    public void Rejects_what_is_no_version(string text) => Assert.False(AndroidReleases.TryParseVersion(text, out _));
}
