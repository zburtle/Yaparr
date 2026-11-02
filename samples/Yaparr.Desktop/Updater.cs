using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Yaparr.Updates;

namespace Yaparr.Desktop;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string Name, string DownloadUrl, string? Sha256, long Size = 0);

public static class Updater
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>Where the steps of an update are written (the log), so a failed update can be understood afterwards.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>Windows ships an MSI and Debian/Ubuntu a .deb; on other systems there is no installer to update from.</summary>
    public static string? Repository
    {
        get
        {
            if (InstallerExtension is null || IsFlatpak) return null;
            var repo = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value;
            return string.IsNullOrWhiteSpace(repo) ? null : repo;
        }
    }

    /// <summary>True when running inside a Flatpak sandbox, which updates the app itself and cannot install a package.</summary>
    public static bool IsFlatpak => OperatingSystem.IsLinux() && File.Exists("/.flatpak-info");

    private static string? InstallerExtension =>
        OperatingSystem.IsWindows() ? ".msi" : OperatingSystem.IsLinux() ? ".deb" : null;

    /// <summary>Debian architecture name of this process, as used in the .deb file name.</summary>
    private static string DebArchitecture => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";

    /// <summary>True if a release asset is the installer for this system.</summary>
    public static bool IsInstaller(string name)
    {
        if (InstallerExtension is not { } ext || !name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return false;
        return ext != ".deb" || name.EndsWith($"_{DebArchitecture}.deb", StringComparison.OrdinalIgnoreCase);
    }

    public static Version CurrentVersion
    {
        get
        {
            var text = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return TryParse(text, out var v) ? v : new Version(0, 0, 0);
        }
    }

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var repo = Repository;
        if (repo is null) return null;

        using var response = await Http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest", ct);
        if (!response.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        if (!TryParse(tag, out var version) || version <= CurrentVersion) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!IsInstaller(name)) continue;

            string? sha = null;
            if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                && UpdateVerifier.IsSha256(d["sha256:".Length..]))
                sha = d["sha256:".Length..];

            var url = asset.GetProperty("browser_download_url").GetString()!;
            if (!UpdateVerifier.IsTrustedDownload(url, repo))
            {
                Log?.Invoke($"update: ignored {name}, its address is not one of {repo}'s release downloads");
                continue;
            }
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            Log?.Invoke($"update: {tag} is available ({name}, {(sha == null ? "no checksum published" : "checksum " + sha[..12] + "...")})");
            long size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var bytes) ? bytes : 0;
            return new UpdateInfo(version, tag!, notes, name, url, sha, size);
        }
        return null;
    }

    /// <param name="progress">Told how far the download is, from 0 to 1.</param>
    public static async Task<string> DownloadAsync(UpdateInfo update, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        // Nothing is installed that cannot be checked against what was published, and only from this project's own releases.
        if (update.Sha256 is not { } expected)
            throw new InvalidDataException("This release has no checksum, so the download cannot be checked. Get it from the releases page instead.");
        if (Repository is not { } repo || !UpdateVerifier.IsTrustedDownload(update.DownloadUrl, repo))
            throw new InvalidDataException("The download address is not one of this project's releases.");

        // In the user's own folder, closed to other users: the package is installed as root on Linux, and a shared
        // folder such as /tmp would let another user swap it after it was checked.
        var dir = Path.Combine(AppData.DefaultDirectory, "update");
        AppData.CreatePrivateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir)) { try { File.Delete(old); } catch (IOException) { } }
        var path = Path.Combine(dir, Path.GetFileName(update.Name));
        // It only gets its real name once it has been checked, so what is called an installer here has always passed.
        var partial = path + ".part";

        try
        {
            using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(partial);
                long total = response.Content.Headers.ContentLength ?? update.Size, done = 0, reported = 0;
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    // About two hundred reports for the whole file are plenty for a bar.
                    if (progress != null && total > 0 && done - reported >= total / 200) { reported = done; progress.Report(Math.Min(1.0, done / (double)total)); }
                }
                progress?.Report(1.0);
            }
            await UpdateVerifier.VerifyAsync(partial, expected, ct);
            File.Move(partial, path, overwrite: true);
        }
        catch (Exception ex)
        {
            try { File.Delete(partial); } catch (IOException) { }
            if (ex is not OperationCanceledException) Log?.Invoke($"update: could not download {update.Name}: {ex.Message}");
            throw;
        }
        Log?.Invoke($"update: downloaded {update.Name} ({new FileInfo(path).Length / 1024} KB) and its checksum matched");
        return path;
    }

    /// <summary>
    /// Starts the installer. On Windows msiexec replaces the app after this process exits, so the caller should shut
    /// down. On Linux the package is installed first (asking for the password through the desktop), and the result says
    /// whether it worked; the caller then restarts the app.
    /// </summary>
    public static async Task<bool> InstallAsync(string path, string sha256, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
        {
            Log?.Invoke("update: starting the installer " + path);
            Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{path}\"") { UseShellExecute = false });
            return true;
        }

        // apt resolves dependencies and treats this as an upgrade; pkexec shows the usual graphical password prompt. What runs as root copies
        // the package where only root can change it, checks it again there and installs that copy (see UpdateVerifier.LinuxInstallScript).
        var psi = new ProcessStartInfo(File.Exists("/usr/bin/pkexec") ? "pkexec" : "/bin/sh") { UseShellExecute = false };
        if (psi.FileName == "pkexec") psi.ArgumentList.Add("/bin/sh");
        foreach (var arg in new[] { "-c", UpdateVerifier.LinuxInstallScript, "yaparr-update", path, sha256, "/var/tmp" }) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the installer.");
        await process.WaitForExitAsync(ct);
        Log?.Invoke("update: the package installer finished with code " + process.ExitCode);
        return process.ExitCode == 0;
    }

    private static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim().TrimStart('v', 'V');
        var end = text.IndexOfAny(['+', '-']);
        if (end >= 0) text = text[..end];
        if (!Version.TryParse(text, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Yaparr-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
