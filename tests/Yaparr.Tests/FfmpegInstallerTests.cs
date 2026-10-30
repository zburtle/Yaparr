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
            Assert.Empty(Directory.GetDirectories(dir).Where(d => Path.GetFileName(d).StartsWith("download-")));
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
    public void The_built_in_package_points_at_a_yaparr_release_and_has_a_hash()
    {
        var p = FfmpegPackage.Default;
        Assert.Equal("https", p.Url.Scheme);
        Assert.Contains("zburtle/Yaparr/releases", p.Url.AbsoluteUri);
        Assert.Equal(64, p.Sha256.Length);
        Assert.EndsWith("ffmpeg.exe", p.ExePathInZip);
    }
}
