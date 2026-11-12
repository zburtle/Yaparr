using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Yaparr.Screen;

/// <summary>A build of FFmpeg that can be downloaded: where from, and what it must look like when it has arrived.</summary>
/// <param name="Version">Names the folder it is installed in, so a newer build can sit next to an older one.</param>
/// <param name="Sha256">The SHA-256 of the downloaded file in hex. A file that does not match is thrown away.</param>
/// <param name="ExePathInZip">Where the program (ffmpeg.exe, or ffmpeg on Linux) is inside the zip.</param>
/// <param name="LicensePathInZip">The licence file, which is kept next to the program.</param>
/// <param name="NoticesRoot">
/// The folder in the zip that holds the program's own notices (LICENSE.txt, SOURCE.txt, BUILD-INFO.txt and a licenses folder,
/// as Yaparr's own build writes them). Everything in it except the bin folder is kept next to the program, so the licences and
/// the note about where the source is travel with it. Without it the licence file is kept and a note is written.
/// </param>
public sealed record FfmpegPackage(string Version, Uri Url, string Sha256, long SizeBytes, string ExePathInZip, string? LicensePathInZip, string? NoticesRoot = null)
{
    /// <summary>
    /// The one build of FFmpeg that Yaparr is known to work with: Yaparr's own trimmed LGPL Windows build (no GPL parts such as
    /// x264; see https://github.com/zburtle/Yaparr.FFmpeg) of commit 17a4a474f4 of FFmpeg's development branch, N-128212 of
    /// 30 October 2026, which has the Graphics Capture fixes of 24 October 2026. No numbered release has those fixes yet (8.1.3
    /// and 9.0.2 capture one frame and stop). It was checked with the FfmpegIntegration tests and the demo. Move to a numbered
    /// release when one includes the fixes. The file is the asset of the Yaparr.FFmpeg release ffmpeg-7 (the same FFmpeg as ffmpeg-3 and later, with AV1 added from ffmpeg-6: the graphics cards' AV1 encoders
    /// and the dav1d decoder; and from ffmpeg-7 Direct3D 12 decoding and scaling on the card, and shared libraries: ffmpeg.exe and FFmpeg's DLLs side by side, which
    /// the app also loads into itself to share a screen without a separate program), which also holds the exact source; see README.md next to this file.
    /// </summary>
    public static FfmpegPackage Default { get; } = new(
        "g17a4a474f4-yaparr-7-win64",
        new Uri("https://github.com/zburtle/Yaparr.FFmpeg/releases/download/ffmpeg-7/ffmpeg-yaparr-g17a4a474f4-win64.zip"),
        "064b72c6bbd13938be3afa203c1c28c351822b404647fd044167ab4d625ef1a9",
        6_652_814,
        "ffmpeg-yaparr-g17a4a474f4-win64/bin/ffmpeg.exe",
        "ffmpeg-yaparr-g17a4a474f4-win64/LICENSE.txt",
        "ffmpeg-yaparr-g17a4a474f4-win64");

    /// <summary>
    /// Yaparr's own Linux x86-64 build, the asset of the Yaparr.FFmpeg release ffmpeg-6 (the same FFmpeg commit as the Windows
    /// one; see https://github.com/zburtle/Yaparr.FFmpeg). It runs on glibc 2.35 or newer and needs the computer's own libva.
    /// </summary>
    public static FfmpegPackage LinuxX64 { get; } = new(
        "g17a4a474f4-yaparr-6-linux-x64",
        new Uri("https://github.com/zburtle/Yaparr.FFmpeg/releases/download/ffmpeg-6/ffmpeg-yaparr-g17a4a474f4-linux-x64.zip"),
        "22c7fdad9d24fe6edeaadcc61203100e8a972f8dfc0893e66da0468bebbb1649",
        2_889_626,
        "ffmpeg-yaparr-g17a4a474f4-linux-x64/bin/ffmpeg",
        "ffmpeg-yaparr-g17a4a474f4-linux-x64/LICENSE.txt",
        "ffmpeg-yaparr-g17a4a474f4-linux-x64");

    /// <summary>Yaparr's own Linux arm64 build, from the same release as <see cref="LinuxX64"/>.</summary>
    public static FfmpegPackage LinuxArm64 { get; } = new(
        "g17a4a474f4-yaparr-6-linux-arm64",
        new Uri("https://github.com/zburtle/Yaparr.FFmpeg/releases/download/ffmpeg-6/ffmpeg-yaparr-g17a4a474f4-linux-arm64.zip"),
        "61968695bf0f356306c66062a078d2e27f4f9b21a04d9425e0632fb11daa270d",
        2_436_218,
        "ffmpeg-yaparr-g17a4a474f4-linux-arm64/bin/ffmpeg",
        "ffmpeg-yaparr-g17a4a474f4-linux-arm64/LICENSE.txt",
        "ffmpeg-yaparr-g17a4a474f4-linux-arm64");

    /// <summary>The build to download for the system this runs on, or null if there is none (another system, or a processor there is no build for).</summary>
    public static FfmpegPackage? ForThisSystem =>
        OperatingSystem.IsWindows() ? Default
        : OperatingSystem.IsLinux() ? RuntimeInformation.OSArchitecture switch { Architecture.X64 => LinuxX64, Architecture.Arm64 => LinuxArm64, _ => null }
        : null;
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
                ExtractLibraries(archive, package.ExePathInZip, staging);
                if (package.LicensePathInZip != null) Extract(archive, package.LicensePathInZip, Path.Combine(staging, "LICENSE.txt"), required: false);
                if (package.NoticesRoot != null) ExtractNotices(archive, package.NoticesRoot, staging);
            }
            // A zip does not always carry the permission to run the program.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path.Combine(staging, ExeName),
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            // A build that brings its own note about where the source is keeps it; otherwise one is written.
            if (!File.Exists(Path.Combine(staging, "SOURCE.txt")))
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

    // The libraries of a shared build (the DLLs, or the .so files) sit in the zip next to the program and must sit next to it afterwards: Windows looks for a
    // DLL in the folder of the program that needs it, and the app loads them from there too. Only files, only of those kinds, only from that folder.
    private static void ExtractLibraries(ZipArchive archive, string exePathInZip, string destination)
    {
        int slash = exePathInZip.LastIndexOf('/');
        string folder = slash < 0 ? "" : exePathInZip[..(slash + 1)];
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || !entry.FullName.StartsWith(folder, StringComparison.Ordinal)) continue;
            string name = entry.FullName[folder.Length..];
            if (name.Contains('/') || name.Length == 0) continue;
            if (!(name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.Contains(".so", StringComparison.Ordinal))) continue;
            entry.ExtractToFile(Path.Combine(destination, name), overwrite: true);
        }
    }

    // Everything under the root except its bin folder (the program is taken from there on its own), with the folders kept. A path
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
