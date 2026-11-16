using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Yaparr.Screen;

namespace Yaparr.Tests;

/// <summary>The download of FFmpeg's libraries: checked by its hash, the libraries kept and nothing else of their folder (no program), the notices kept with them.</summary>
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

    private static readonly string Codec = OperatingSystem.IsWindows() ? "avcodec-63.dll" : "libavcodec.so.63";
    private static readonly string Util = OperatingSystem.IsWindows() ? "avutil-61.dll" : "libavutil.so.61";

    private static FfmpegPackage Package(byte[] zip, string? sha = null) => new(
        "test-1", new Uri("https://example.invalid/ffmpeg.zip"), sha ?? Convert.ToHexString(SHA256.HashData(zip)), zip.Length,
        "ff/bin/" + Codec, "ff/LICENSE.txt");

    private static string Temp() => Path.Combine(Path.GetTempPath(), "yaparr-ffinst-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_build_that_brings_its_own_notices_keeps_them_all_next_to_the_libraries()
    {
        var dir = Temp();
        try
        {
            var zip = Zip(("ff/bin/" + Codec, "library"), ("ff/LICENSE.txt", "lgpl"), ("ff/SOURCE.txt", "source is in the release"), ("ff/BUILD-INFO.txt", "configure..."),
                ("ff/licenses/libvpx/LICENSE", "bsd"), ("ff/licenses/libvpx/PATENTS", "grant"), ("other/LICENSE.txt", "not this one"));
            var package = Package(zip) with { NoticesRoot = "ff" };
            var folder = await FfmpegInstaller.InstallAsync(package, null, dir, new Serve(zip));

            Assert.Equal("library", File.ReadAllText(Path.Combine(folder, Codec)));
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
            var zip = Zip(("ff/bin/" + Codec, "library"), ("ff/../../escaped.txt", "x"));
            var package = Package(zip) with { NoticesRoot = "ff" };
            await Assert.ThrowsAsync<InvalidDataException>(() => FfmpegInstaller.InstallAsync(package, null, dir, new Serve(zip)));
            Assert.Null(FfmpegInstaller.FindAny(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_download_with_the_expected_hash_is_unpacked_to_its_libraries_and_found_again()
    {
        var dir = Temp();
        try
        {
            // An older package also has the ffmpeg program in its bin folder: it is left out, since nothing runs it.
            var zip = Zip(("ff/bin/" + Codec, "library"), ("ff/bin/" + Util, "library"), ("ff/bin/ffmpeg.exe", "program"), ("ff/bin/ffmpeg", "program"),
                ("ff/bin/notes.txt", "not a library"), ("ff/bin/sub/other.dll", "not in that folder"), ("ff/LICENSE.txt", "lgpl"));
            var package = Package(zip);
            var serve = new Serve(zip);
            var steps = new List<double>();

            Assert.Null(FfmpegInstaller.Installed(package, dir));
            var folder = await FfmpegInstaller.InstallAsync(package, new Progress<double>(steps.Add), dir, serve);

            Assert.True(File.Exists(Path.Combine(folder, Codec)));
            Assert.True(File.Exists(Path.Combine(folder, Util)));
            Assert.False(File.Exists(Path.Combine(folder, "ffmpeg.exe")), "the program is not kept");
            Assert.False(File.Exists(Path.Combine(folder, "ffmpeg")), "the program is not kept");
            Assert.False(File.Exists(Path.Combine(folder, "notes.txt")), "only libraries");
            Assert.False(File.Exists(Path.Combine(folder, "other.dll")), "only from libavcodec's own folder in the zip");
            Assert.Equal(folder, FfmpegInstaller.Installed(package, dir));
            Assert.Equal(folder, FfmpegInstaller.FindAny(dir));
            // The app finds them there when they are not next to it.
            Assert.Equal(Path.GetFullPath(folder), FfmpegLibraries.Find(Path.Combine(dir, "app"), explicitFolder: Path.Combine(dir, "nothing"), installDirectory: dir));
            Assert.Equal("lgpl", File.ReadAllText(Path.Combine(folder, "LICENSE.txt")));
            Assert.Contains("Yaparr.FFmpeg", File.ReadAllText(Path.Combine(folder, "SOURCE.txt")));
            Assert.DoesNotContain(Directory.GetDirectories(dir), d => Path.GetFileName(d).StartsWith("download-"));
            Assert.Equal(1.0, steps.Last());

            // Already there: nothing is fetched again.
            await FfmpegInstaller.InstallAsync(package, null, dir, serve);
            Assert.Equal(1, serve.Requests);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_folder_of_an_older_build_with_only_the_program_in_it_does_not_count()
    {
        var dir = Temp();
        try
        {
            var old = Directory.CreateDirectory(Path.Combine(dir, "g17a4a474f4-yaparr-3-win64")).FullName;
            File.WriteAllText(Path.Combine(old, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"), "program");
            Assert.Null(FfmpegInstaller.FindAny(dir));
            Assert.Null(FfmpegLibraries.Find(Path.Combine(dir, "app"), explicitFolder: Path.Combine(dir, "nothing"), installDirectory: dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_download_with_another_hash_is_refused_and_leaves_nothing_behind()
    {
        var dir = Temp();
        try
        {
            var zip = Zip(("ff/bin/" + Codec, "library"));
            var package = Package(zip, sha: new string('0', 64));
            await Assert.ThrowsAsync<InvalidDataException>(() => FfmpegInstaller.InstallAsync(package, null, dir, new Serve(zip)));
            Assert.Null(FfmpegInstaller.Installed(package, dir));
            Assert.Null(FfmpegInstaller.FindAny(dir));
            Assert.Empty(Directory.GetDirectories(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_download_without_the_libraries_in_it_is_refused()
    {
        var dir = Temp();
        try
        {
            var zip = Zip(("ff/bin/ffmpeg.exe", "only the program"));
            await Assert.ThrowsAsync<InvalidDataException>(() => FfmpegInstaller.InstallAsync(Package(zip), null, dir, new Serve(zip)));
            Assert.Null(FfmpegInstaller.FindAny(dir));
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
            // Never the Windows build: Linux libraries, from the Yaparr.FFmpeg release, with a hash.
            Assert.NotNull(p);
            Assert.DoesNotContain("win64", p!.Url.AbsoluteUri);
            Assert.Contains("linux", p.Url.AbsoluteUri);
            Assert.EndsWith("/bin/libavcodec.so.63", p.LibraryPathInZip);
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
            Assert.Equal(p.NoticesRoot + "/bin/libavcodec.so.63", p.LibraryPathInZip);
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
        Assert.Equal(p.NoticesRoot + "/bin/avcodec-63.dll", p.LibraryPathInZip);
    }
}
