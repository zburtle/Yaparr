using System.IO.Compression;
using System.Security.Cryptography;

namespace Yaparr.Screen;

/// <summary>A build of FFmpeg that can be downloaded: where from, and what it must look like when it has arrived.</summary>
/// <param name="Version">Names the folder it is installed in, so a newer build can sit next to an older one.</param>
/// <param name="Sha256">The SHA-256 of the downloaded file in hex. A file that does not match is thrown away.</param>
/// <param name="ExePathInZip">Where ffmpeg.exe is inside the zip.</param>
/// <param name="LicensePathInZip">The licence file, which is kept next to the program.</param>
public sealed record FfmpegPackage(string Version, Uri Url, string Sha256, long SizeBytes, string ExePathInZip, string? LicensePathInZip)
{
    /// <summary>
    /// The one build of FFmpeg that Yaparr is known to work with: an LGPL Windows build (no GPL parts such as x264) with libvpx
    /// and a gfxcapture filter that has the Graphics Capture fixes of 24 October 2026. No numbered release has those fixes
    /// yet (8.1.3 and 9.0.2 capture one frame and stop), so this is a build of FFmpeg's development branch, N-128212 of
    /// 30 October 2026, checked with the FfmpegIntegration tests and the demo. Move to a numbered release when one includes
    /// the fixes. The file is meant to be copied to a Yaparr release (tag ffmpeg-1) so that it stays put; see README.md next
    /// to this file.
    /// </summary>
    public static FfmpegPackage Default { get; } = new(
        "n128212-lgpl-win64",
        new Uri("https://github.com/zburtle/Yaparr/releases/download/ffmpeg-1/ffmpeg-master-latest-win64-lgpl.zip"),
        "de1d5f6ae7c16d264ab5bbeaa906e23f5a42b4edfab105980595524591a3f8a5",
        175_268_886,
        "ffmpeg-master-latest-win64-lgpl/bin/ffmpeg.exe",
        "ffmpeg-master-latest-win64-lgpl/LICENSE.txt");
}

/// <summary>Downloads FFmpeg once into the user's own application data, checks it, and keeps it there.</summary>
public static class FfmpegInstaller
{
    /// <summary>The folder under which each version gets its own folder.</summary>
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yaparr", "ffmpeg");

    private static string ExeName => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    /// <summary>The installed program for this package, or null if it has not been installed.</summary>
    public static string? Installed(FfmpegPackage package, string? directory = null)
    {
        var exe = Path.Combine(directory ?? DefaultDirectory, package.Version, ExeName);
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>The newest installed program of any version, or null.</summary>
    public static string? FindAny(string? directory = null)
    {
        directory ??= DefaultDirectory;
        if (!Directory.Exists(directory)) return null;
        return Directory.EnumerateDirectories(directory)
            .Select(d => Path.Combine(d, ExeName))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// Downloads and installs the package unless it is there already, and returns the path of ffmpeg. Progress goes from 0 to 1.
    /// A download that does not have the expected hash is refused, and nothing is installed from it.
    /// </summary>
    public static async Task<string> InstallAsync(FfmpegPackage package, IProgress<double>? progress = null, string? directory = null,
        HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        directory ??= DefaultDirectory;
        if (Installed(package, directory) is { } have) return have;

        Directory.CreateDirectory(directory);
        var work = Path.Combine(directory, "download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var zip = Path.Combine(work, "ffmpeg.zip");
            using (var http = handler == null ? new HttpClient() : new HttpClient(handler))
            {
                http.Timeout = TimeSpan.FromMinutes(30);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Yaparr");
                using var response = await http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? package.SizeBytes;
                using var hash = SHA256.Create();
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = File.Create(zip);
                var buffer = new byte[128 * 1024];
                long done = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    hash.TransformBlock(buffer, 0, n, null, 0);
                    done += n;
                    if (total > 0) progress?.Report(Math.Min(0.95, done / (double)total));
                }
                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var actual = Convert.ToHexString(hash.Hash!);
                if (!actual.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"The downloaded FFmpeg does not match the expected file (got {actual[..12]}..., wanted {package.Sha256[..12]}...), so it was not installed.");
            }

            var target = Path.Combine(directory, package.Version);
            var staging = Path.Combine(work, "unpacked");
            Directory.CreateDirectory(staging);
            using (var archive = ZipFile.OpenRead(zip))
            {
                Extract(archive, package.ExePathInZip, Path.Combine(staging, ExeName));
                if (package.LicensePathInZip != null) Extract(archive, package.LicensePathInZip, Path.Combine(staging, "LICENSE.txt"), required: false);
            }
            File.WriteAllText(Path.Combine(staging, "SOURCE.txt"),
                $"FFmpeg ({package.Version}), downloaded from {package.Url}.{Environment.NewLine}" +
                $"It is licensed under the LGPL; see LICENSE.txt. The source code and build scripts are at https://github.com/BtbN/FFmpeg-Builds and https://ffmpeg.org.{Environment.NewLine}");

            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
            progress?.Report(1);
            return Path.Combine(target, ExeName);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void Extract(ZipArchive archive, string entryPath, string destination, bool required = true)
    {
        var entry = archive.GetEntry(entryPath);
        if (entry == null)
        {
            if (required) throw new InvalidDataException($"The download has no '{entryPath}' in it.");
            return;
        }
        entry.ExtractToFile(destination, overwrite: true);
    }
}
