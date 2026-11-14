using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Yaparr.Screen;

/// <summary>A build of FFmpeg's libraries that can be downloaded: where from, and what it must look like when it has arrived.</summary>
/// <param name="Version">Names the folder it is installed in, so a newer build can sit next to an older one.</param>
/// <param name="Sha256">The SHA-256 of the downloaded file in hex. A file that does not match is thrown away.</param>
/// <param name="LibraryPathInZip">
/// Where libavcodec is inside the zip (avcodec-63.dll, or libavcodec.so.63 on Linux). The libraries are the files of that folder that are libraries (the DLLs or
/// the .so files); anything else there (an ffmpeg program, in older packages) is left out, since nothing runs it.
/// </param>
/// <param name="LicensePathInZip">The licence file, which is kept next to the libraries.</param>
/// <param name="NoticesRoot">
/// The folder in the zip that holds the build's own notices (LICENSE.txt, SOURCE.txt, BUILD-INFO.txt and a licenses folder,
/// as Yaparr's own build writes them). Everything in it except the bin folder is kept next to the libraries, so the licences and
/// the note about where the source is travel with them. Without it the licence file is kept and a note is written.
/// </param>
public sealed record FfmpegPackage(string Version, Uri Url, string Sha256, long SizeBytes, string LibraryPathInZip, string? LicensePathInZip, string? NoticesRoot = null)
{
    /// <summary>
    /// The one build of FFmpeg that Yaparr is known to work with: Yaparr's own trimmed LGPL Windows build (no GPL parts such as
    /// x264; see https://github.com/zburtle/Yaparr.FFmpeg) of commit 17a4a474f4 of FFmpeg's development branch, N-128212 of
    /// 30 October 2026, which has the Graphics Capture fixes of 24 October 2026. No numbered release has those fixes yet (8.1.3
    /// and 9.0.2 capture one frame and stop). It was checked with the FfmpegIntegration tests and the demo. Move to a numbered
    /// release when one includes the fixes. The file is the asset of the Yaparr.FFmpeg release ffmpeg-7 (the same FFmpeg as ffmpeg-3 and later, with AV1 added from ffmpeg-6: the graphics cards' AV1 encoders
    /// and the dav1d decoder; and from ffmpeg-7 Direct3D 12 decoding and scaling on the card, and shared libraries, FFmpeg's DLLs (the ffmpeg.exe beside them in the zip is left out), which
    /// the app also loads into itself to share a screen without a separate program), which also holds the exact source; see README.md next to this file.
    /// </summary>
    public static FfmpegPackage Default { get; } = new(
        "g17a4a474f4-yaparr-7-win64",
        new Uri("https://github.com/zburtle/Yaparr.FFmpeg/releases/download/ffmpeg-7/ffmpeg-yaparr-g17a4a474f4-win64.zip"),
        "064b72c6bbd13938be3afa203c1c28c351822b404647fd044167ab4d625ef1a9",
        6_652_814,
        "ffmpeg-yaparr-g17a4a474f4-win64/bin/avcodec-63.dll",
        "ffmpeg-yaparr-g17a4a474f4-win64/LICENSE.txt",
        "ffmpeg-yaparr-g17a4a474f4-win64");

    /// <summary>
    /// Yaparr's own Linux x86-64 build, the asset of the Yaparr.FFmpeg release ffmpeg-8 (the same FFmpeg commit as the Windows
    /// one; see https://github.com/zburtle/Yaparr.FFmpeg): FFmpeg's shared libraries (the ffmpeg program in the zip is left out) (libavcodec.so.63 and the others), which Yaparr
    /// loads into itself to encode. It runs on glibc 2.35 or newer and needs the computer's own libva.
    /// </summary>
    public static FfmpegPackage LinuxX64 { get; } = new(
        "g17a4a474f4-yaparr-8-linux-x64",
        new Uri("https://github.com/zburtle/Yaparr.FFmpeg/releases/download/ffmpeg-8/ffmpeg-yaparr-g17a4a474f4-linux-x64.zip"),
        "96e033518b6689fceba74bef7b4b1ce8ae2d6ffd2aa52a33bb1be749cfc11e07",
        3_248_032,
        "ffmpeg-yaparr-g17a4a474f4-linux-x64/bin/libavcodec.so.63",
        "ffmpeg-yaparr-g17a4a474f4-linux-x64/LICENSE.txt",
        "ffmpeg-yaparr-g17a4a474f4-linux-x64");

    /// <summary>Yaparr's own Linux arm64 build, from the same release as <see cref="LinuxX64"/>.</summary>
    public static FfmpegPackage LinuxArm64 { get; } = new(
        "g17a4a474f4-yaparr-8-linux-arm64",
        new Uri("https://github.com/zburtle/Yaparr.FFmpeg/releases/download/ffmpeg-8/ffmpeg-yaparr-g17a4a474f4-linux-arm64.zip"),
        "f4069482ae02efe3168b5d3b5be5d1d66993b0dd26a3719d8e27c31a63762ec7",
        2_773_411,
        "ffmpeg-yaparr-g17a4a474f4-linux-arm64/bin/libavcodec.so.63",
        "ffmpeg-yaparr-g17a4a474f4-linux-arm64/LICENSE.txt",
        "ffmpeg-yaparr-g17a4a474f4-linux-arm64");

    /// <summary>The build to download for the system this runs on, or null if there is none (another system, or a processor there is no build for).</summary>
    public static FfmpegPackage? ForThisSystem =>
        OperatingSystem.IsWindows() ? Default
        : OperatingSystem.IsLinux() ? RuntimeInformation.OSArchitecture switch { Architecture.X64 => LinuxX64, Architecture.Arm64 => LinuxArm64, _ => null }
        : null;
}

/// <summary>Downloads FFmpeg's libraries once into the user's own application data, checks them, and keeps them there.</summary>
public static class FfmpegInstaller
{
    /// <summary>The folder under which each version gets its own folder.</summary>
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yaparr", "ffmpeg");

    /// <summary>Whether a folder has FFmpeg's libraries in it (libavcodec, by whichever version it has).</summary>
    public static bool HasLibraries(string folder)
    {
        try { return Directory.Exists(folder) && Directory.EnumerateFiles(folder, OperatingSystem.IsWindows() ? "avcodec-*.dll" : "libavcodec.so*").Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The folder this package's libraries are installed in, or null if it has not been installed.</summary>
    public static string? Installed(FfmpegPackage package, string? directory = null)
    {
        var folder = Path.Combine(directory ?? DefaultDirectory, package.Version);
        return File.Exists(Path.Combine(folder, Path.GetFileName(package.LibraryPathInZip))) ? folder : null;
    }

    /// <summary>The folder of the newest installed libraries of any version, or null. (A folder from an older build with only the program in it does not count.)</summary>
    public static string? FindAny(string? directory = null)
    {
        directory ??= DefaultDirectory;
        if (!Directory.Exists(directory)) return null;
        return Directory.EnumerateDirectories(directory)
            .Where(HasLibraries)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// Downloads and installs the package unless it is there already, and returns the folder of the libraries. Progress goes from 0 to 1.
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
                if (archive.GetEntry(package.LibraryPathInZip) == null) throw new InvalidDataException($"The download has no '{package.LibraryPathInZip}' in it.");
                ExtractLibraries(archive, package.LibraryPathInZip, staging);
                if (package.LicensePathInZip != null) Extract(archive, package.LicensePathInZip, Path.Combine(staging, "LICENSE.txt"), required: false);
                if (package.NoticesRoot != null) ExtractNotices(archive, package.NoticesRoot, staging);
            }
            // A build that brings its own note about where the source is keeps it; otherwise one is written.
            if (!File.Exists(Path.Combine(staging, "SOURCE.txt")))
                File.WriteAllText(Path.Combine(staging, "SOURCE.txt"),
                    $"FFmpeg's libraries ({package.Version}), downloaded from {package.Url}.{Environment.NewLine}" +
                    $"They are licensed under the LGPL; see LICENSE.txt. The source code and build scripts are at https://github.com/zburtle/Yaparr.FFmpeg and https://ffmpeg.org.{Environment.NewLine}");

            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
            progress?.Report(1);
            return target;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // The libraries (the DLLs, or the .so files) of the folder libavcodec is in, and nothing else of it: the app loads them from where they land.
    private static void ExtractLibraries(ZipArchive archive, string libraryPathInZip, string destination)
    {
        int slash = libraryPathInZip.LastIndexOf('/');
        string folder = slash < 0 ? "" : libraryPathInZip[..(slash + 1)];
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || !entry.FullName.StartsWith(folder, StringComparison.Ordinal)) continue;
            string name = entry.FullName[folder.Length..];
            if (name.Contains('/') || name.Length == 0) continue;
            if (!(name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.Contains(".so", StringComparison.Ordinal))) continue;
            entry.ExtractToFile(Path.Combine(destination, name), overwrite: true);
        }
    }

    // Everything under the root except its bin folder (the libraries are taken from there on their own), with the folders kept. A path
    // that would land outside the destination is refused.
    private static void ExtractNotices(ZipArchive archive, string root, string destination)
    {
        root = root.TrimEnd('/') + "/";
        var full = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(root, StringComparison.Ordinal) || entry.FullName.EndsWith('/')) continue;
            var relative = entry.FullName[root.Length..];
            if (relative.StartsWith("bin/", StringComparison.Ordinal)) continue;
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(full, StringComparison.Ordinal)) throw new InvalidDataException($"The download has an unsafe path '{entry.FullName}'.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
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
