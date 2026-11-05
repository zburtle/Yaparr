using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Yaparr.Screen;

namespace Yaparr.Tests;

public class FfmpegInstallerTests
{
    private sealed class Serve : HttpMessageHandler
    {
        private readonly byte[] _body;
        public int Requests;
        public Serve(byte[] body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_body) });
        }
    }

    private static byte[] Zip(params (string Path, string Text)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, text) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open());
                w.Write(text);
            }
        return ms.ToArray();
    }

    private static FfmpegPackage Package(byte[] zip, string? sha = null) => new(
        "test-1", new Uri("https://example.invalid/ffmpeg.zip"), sha ?? Convert.ToHexString(SHA256.HashData(zip)), zip.Length,
        "ff/bin/ffmpeg.exe", "ff/LICENSE.txt");

    [Fact]
    public async Task A_build_that_brings_its_own_notices_keeps_them_all_next_to_the_program()
    {
        var dir = Temp();
        try
        {
            var zip = Zip(("ff/bin/ffmpeg.exe", "program"), ("ff/LICENSE.txt", "lgpl"), ("ff/SOURCE.txt", "source is in the release"), ("ff/BUILD-INFO.txt", "configure..."),
                ("ff/licenses/libvpx/LICENSE", "bsd"), ("ff/licenses/libvpx/PATENTS", "grant"), ("other/LICENSE.txt", "not this one"));
            var package = Package(zip) with { NoticesRoot = "ff" };
            var path = await FfmpegInstaller.InstallAsync(package, null, dir, new Serve(zip));
            var folder = Path.GetDirectoryName(path)!;

            Assert.Equal("program", File.ReadAllText(path));
            Assert.Equal("lgpl", File.ReadAllText(Path.Combine(folder, "LICENSE.txt")));
            Assert.Equal("source is in the release", File.ReadAllText(Path.Combine(folder, "SOURCE.txt")));   // theirs, not ours
            Assert.Equal("configure...", File.ReadAllText(Path.Combine(folder, "BUILD-INFO.txt")));
            Assert.Equal("bsd", File.ReadAllText(Path.Combine(folder, "licenses", "libvpx", "LICENSE")));
            Assert.Equal("grant", File.ReadAllText(Path.Combine(folder, "licenses", "libvpx", "PATENTS")));
            Assert.False(Directory.Exists(Path.Combine(folder, "bin")));
            Assert.DoesNotContain("not this one", Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_path_that_climbs_out_of_the_folder_is_refused()
    {
        var dir = Temp();
        try
        {
            var zip = Zip(("ff/bin/ffmpeg.exe", "program"), ("ff/../../escaped.txt", "x"));
            var package = Package(zip) with { NoticesRoot = "ff" };
            await Assert.ThrowsAsync<InvalidDataException>(() => FfmpegInstaller.InstallAsync(package, null, dir, new Serve(zip)));
            Assert.Null(FfmpegInstaller.FindAny(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    private static string Temp() => Path.Combine(Path.GetTempPath(), "yaparr-ffinst-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_download_with_the_expected_hash_is_unpacked_and_found_again()
    {
        var dir = Temp();
        try
        {
            var zip = Zip(("ff/bin/ffmpeg.exe", "program"), ("ff/bin/ffplay.exe", "not wanted"), ("ff/LICENSE.txt", "lgpl"));
            var package = Package(zip);
            var serve = new Serve(zip);
            var steps = new List<double>();

            Assert.Null(FfmpegInstaller.Installed(package, dir));
            var path = await FfmpegInstaller.InstallAsync(package, new Progress<double>(steps.Add), dir, serve);

            Assert.Equal("program", File.ReadAllText(path));
            Assert.Equal(path, FfmpegInstaller.Installed(package, dir));
            Assert.Equal(path, FfmpegInstaller.FindAny(dir));
            Assert.Equal(path, FfmpegLocator.Find(Path.Combine(dir, "app"), "", Path.Combine(dir, "missing"), dir));
            Assert.Equal("lgpl", File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "LICENSE.txt")));
            Assert.Contains("BtbN", File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "SOURCE.txt")));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "ffplay.exe")));
            Assert.DoesNotContain(Directory.GetDirectories(dir), d => Path.GetFileName(d).StartsWith("download-"));
            Assert.Equal(1.0, steps.Last());

            // Already there: nothing is fetched again.
            await FfmpegInstaller.InstallAsync(package, null, dir, serve);
            Assert.Equal(1, serve.Requests);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_download_with_another_hash_is_refused_and_leaves_nothing_behind()
    {
        var dir = Temp();
        try
        {
            var zip = Zip(("ff/bin/ffmpeg.exe", "program"));
            var package = Package(zip, sha: new string('0', 64));
            await Assert.ThrowsAsync<InvalidDataException>(() => FfmpegInstaller.InstallAsync(package, null, dir, new Serve(zip)));
            Assert.Null(FfmpegInstaller.Installed(package, dir));
            Assert.Null(FfmpegInstaller.FindAny(dir));
            Assert.Empty(Directory.GetDirectories(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_download_without_the_program_in_it_is_refused()
    {
        var dir = Temp();
        try
        {
            var zip = Zip(("ff/readme.txt", "nothing"));
            await Assert.ThrowsAsync<InvalidDataException>(() => FfmpegInstaller.InstallAsync(Package(zip), null, dir, new Serve(zip)));
            Assert.Null(FfmpegInstaller.FindAny(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task On_linux_the_installed_program_can_be_run()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Temp();
        try
        {
            var zip = Zip(("ff/bin/ffmpeg", "program"), ("ff/LICENSE.txt", "lgpl"));
            var package = Package(zip) with { ExePathInZip = "ff/bin/ffmpeg" };
            var path = await FfmpegInstaller.InstallAsync(package, null, dir, new Serve(zip));
            Assert.Equal("ffmpeg", Path.GetFileName(path));
            Assert.True(File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_download_is_chosen_for_the_system()
    {
        var p = FfmpegPackage.ForThisSystem;
        if (OperatingSystem.IsWindows()) Assert.Same(FfmpegPackage.Default, p);
        else if (OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture is System.Runtime.InteropServices.Architecture.X64 or System.Runtime.InteropServices.Architecture.Arm64)
        {
            // Never the Windows build: a Linux program, from the Yaparr.FFmpeg release, with a hash.
            Assert.NotNull(p);
            Assert.DoesNotContain("win64", p!.Url.AbsoluteUri);
            Assert.Contains("linux", p.Url.AbsoluteUri);
            Assert.EndsWith("/bin/ffmpeg", p.ExePathInZip);
        }
    }

    [Fact]
    public void The_linux_packages_point_at_a_yaparr_release_and_have_hashes()
    {
        foreach (var p in new[] { FfmpegPackage.LinuxX64, FfmpegPackage.LinuxArm64 })
        {
            Assert.Equal("https", p.Url.Scheme);
            Assert.Contains("zburtle/Yaparr.FFmpeg/releases", p.Url.AbsoluteUri);
            Assert.Equal(64, p.Sha256.Length);
            Assert.StartsWith(p.NoticesRoot + "/bin/", p.ExePathInZip);
            Assert.Equal(p.NoticesRoot + "/LICENSE.txt", p.LicensePathInZip);
        }
        Assert.NotEqual(FfmpegPackage.LinuxX64.Sha256, FfmpegPackage.LinuxArm64.Sha256);
    }

    [Fact]
    public void The_built_in_package_points_at_a_yaparr_release_and_has_a_hash()
    {
        var p = FfmpegPackage.Default;
        Assert.Equal("https", p.Url.Scheme);
        Assert.Contains("zburtle/Yaparr.FFmpeg/releases", p.Url.AbsoluteUri);
        Assert.Equal(64, p.Sha256.Length);
        Assert.EndsWith("ffmpeg.exe", p.ExePathInZip);
    }
}
