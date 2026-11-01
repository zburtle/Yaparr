using System.Security.Cryptography;
using Yaparr.Updates;

namespace Yaparr.Tests;

public class UpdateVerifierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-updtest-" + Guid.NewGuid().ToString("N"));

    public UpdateVerifierTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Theory]
    [InlineData("https://github.com/zburtle/Yaparr/releases/download/v1.7.1/Yaparr-1.7.1-x64.msi", true)]
    [InlineData("https://GitHub.com/zburtle/yaparr/releases/download/v1.7.1/a.msi", true)]
    [InlineData("http://github.com/zburtle/Yaparr/releases/download/v1.7.1/a.msi", false)]
    [InlineData("https://github.com.evil.example/zburtle/Yaparr/releases/download/v1.7.1/a.msi", false)]
    [InlineData("https://evil.example/zburtle/Yaparr/releases/download/v1.7.1/a.msi", false)]
    [InlineData("https://github.com/someone-else/Yaparr/releases/download/v1.7.1/a.msi", false)]
    [InlineData("https://github.com/zburtle/Yaparr/archive/main.zip", false)]
    [InlineData("https://user:pw@github.com/zburtle/Yaparr/releases/download/v1/a.msi", false)]
    [InlineData("https://github.com:8443/zburtle/Yaparr/releases/download/v1/a.msi", false)]
    [InlineData("not an address", false)]
    [InlineData(null, false)]
    public void Only_this_projects_release_downloads_are_trusted(string? url, bool trusted) =>
        Assert.Equal(trusted, UpdateVerifier.IsTrustedDownload(url, "zburtle/Yaparr"));

    [Fact]
    public void A_checksum_is_sixty_four_hex_digits()
    {
        Assert.True(UpdateVerifier.IsSha256(new string('a', 64)));
        Assert.True(UpdateVerifier.IsSha256("C2A2363DC3BEB76249EFE44EF1F0EED0E29A32A19A0F130F0AE948FC8B9AB143"));
        Assert.False(UpdateVerifier.IsSha256(new string('a', 63)));
        Assert.False(UpdateVerifier.IsSha256(new string('g', 64)));
        Assert.False(UpdateVerifier.IsSha256(null));
    }

    [Fact]
    public async Task A_file_that_matches_is_kept_whatever_the_case_of_the_checksum()
    {
        var path = Path.Combine(_dir, "ok.msi");
        var bytes = RandomNumberGenerator.GetBytes(100_000);
        await File.WriteAllBytesAsync(path, bytes);
        var sha = Convert.ToHexString(SHA256.HashData(bytes));

        await UpdateVerifier.VerifyAsync(path, sha);
        await UpdateVerifier.VerifyAsync(path, sha.ToLowerInvariant());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task A_file_that_does_not_match_is_refused_and_deleted()
    {
        var path = Path.Combine(_dir, "bad.msi");
        await File.WriteAllBytesAsync(path, RandomNumberGenerator.GetBytes(100_000));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => UpdateVerifier.VerifyAsync(path, new string('0', 64)));
        Assert.Contains("checksum", ex.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_file_changed_by_one_byte_is_refused()
    {
        var path = Path.Combine(_dir, "flipped.msi");
        var bytes = RandomNumberGenerator.GetBytes(50_000);
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        bytes[25_000] ^= 1;
        await File.WriteAllBytesAsync(path, bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateVerifier.VerifyAsync(path, sha));
    }
}
