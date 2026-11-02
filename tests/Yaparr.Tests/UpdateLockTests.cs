using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Yaparr.Desktop;
using Yaparr.Updates;

namespace Yaparr.Tests;

/// <summary>The installer is checked through a handle that keeps other programs from changing it until it has been started.</summary>
public class UpdateLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-lock-" + Guid.NewGuid().ToString("N"));

    public UpdateLockTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private async Task<(string Path, string Sha)> MakeFile(string name = "a.msi")
    {
        var path = Path.Combine(_dir, name);
        var bytes = RandomNumberGenerator.GetBytes(200_000);
        await File.WriteAllBytesAsync(path, bytes);
        return (path, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    [Fact]
    public async Task A_file_that_matches_is_opened_from_its_start_whatever_the_case_of_the_checksum()
    {
        var (path, sha) = await MakeFile();
        await using var file = await UpdateVerifier.OpenVerifiedAsync(path, sha.ToLowerInvariant());
        Assert.Equal(0, file.Position);
        Assert.Equal(sha, Convert.ToHexString(await SHA256.HashDataAsync(file)));
    }

    [Fact]
    public async Task A_file_that_does_not_match_is_refused_deleted_and_not_left_open()
    {
        var (path, _) = await MakeFile();
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => UpdateVerifier.OpenVerifiedAsync(path, new string('0', 64)));
        Assert.Contains("checksum", ex.Message);
        Assert.False(File.Exists(path));
    }

    // Windows says "in use" as an IOException for some calls and as an access error for others.
    private static void Refused(Action change)
    {
        var ex = Record.Exception(change);
        Assert.True(ex is IOException or UnauthorizedAccessException, "expected the change to be refused, but got " + (ex?.GetType().Name ?? "no error"));
    }

    [Fact]
    public async Task While_it_is_open_nobody_can_write_to_it_delete_it_or_rename_it_but_they_can_read_it()
    {
        if (!OperatingSystem.IsWindows()) return; // sharing modes are enforced by Windows

        var (path, sha) = await MakeFile();
        var file = await UpdateVerifier.OpenVerifiedAsync(path, sha);
        try
        {
            Refused(() => File.WriteAllText(path, "swapped"));
            Refused(() => File.Delete(path));
            Refused(() => File.Move(path, path + ".other"));
            var replacement = Path.Combine(_dir, "replacement.msi");
            File.WriteAllText(replacement, "swapped");
            Refused(() => File.Move(replacement, path, overwrite: true));
            using var reader = File.OpenRead(path); // msiexec only needs to read
            Assert.Equal(200_000, reader.Length);
        }
        finally { await file.DisposeAsync(); }

        File.WriteAllText(path, "free again");
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Another_program_holding_the_file_is_seen_and_this_one_is_not()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (path, sha) = await MakeFile();
        await using var mine = await UpdateVerifier.OpenVerifiedAsync(path, sha);
        Assert.Empty(FileHolders.Others(path));

        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-Command", $"$f=[IO.File]::Open('{path}','Open','Read','Read'); Start-Sleep 30" }) psi.ArgumentList.Add(a);
        using var other = Process.Start(psi)!;
        try
        {
            await TestUtil.WaitFor(() => FileHolders.Others(path).Contains(other.Id));
            Assert.DoesNotContain(Environment.ProcessId, FileHolders.Others(path));
        }
        finally { other.Kill(true); }
    }
}
