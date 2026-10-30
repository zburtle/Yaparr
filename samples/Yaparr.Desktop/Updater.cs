using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Yaparr.Desktop;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string Name, string DownloadUrl, string? Sha256);

public static class Updater
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>Windows ships an MSI and Debian/Ubuntu a .deb; on other systems there is no installer to update from.</summary>
    public static string? Repository
    {
        get
        {
            if (InstallerExtension is null) return null;
            var repo = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value;
            return string.IsNullOrWhiteSpace(repo) ? null : repo;
        }
    }

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
            if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = d["sha256:".Length..];

            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            return new UpdateInfo(version, tag!, notes, name, asset.GetProperty("browser_download_url").GetString()!, sha);
        }
        return null;
    }

    public static async Task<string> DownloadAsync(UpdateInfo update, CancellationToken ct = default)
    {
        // In the user's own folder, closed to other users: the package is installed as root on Linux, and a shared
        // folder such as /tmp would let another user swap it after it was checked.
        var dir = Path.Combine(AppData.DefaultDirectory, "update");
        AppData.CreatePrivateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir)) { try { File.Delete(old); } catch (IOException) { } }
        var path = Path.Combine(dir, Path.GetFileName(update.Name));

        using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(path);
            await source.CopyToAsync(target, ct);
        }

        if (update.Sha256 is not null)
        {
            await using var file = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
            if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                throw new InvalidDataException("The downloaded installer failed its checksum.");
            }
        }
        return path;
    }

    /// <summary>
    /// Starts the installer. On Windows msiexec replaces the app after this process exits, so the caller should shut
    /// down. On Linux the package is installed first (asking for the password through the desktop), and the result says
    /// whether it worked; the caller then restarts the app.
    /// </summary>
    public static async Task<bool> InstallAsync(string path, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{path}\"") { UseShellExecute = false });
            return true;
        }

        // apt resolves dependencies and treats this as an upgrade; pkexec shows the usual graphical password prompt.
        var psi = new ProcessStartInfo(File.Exists("/usr/bin/pkexec") ? "pkexec" : "apt-get") { UseShellExecute = false };
        if (psi.FileName == "pkexec") psi.ArgumentList.Add("apt-get");
        foreach (var arg in new[] { "install", "-y", "--allow-downgrades", path }) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the installer.");
        await process.WaitForExitAsync(ct);
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
