using System.Security.Cryptography;

namespace Yaparr.Updates;

/// <summary>The checks an update has to pass before it is run: where it came from, and that what arrived is what was published.</summary>
public static class UpdateVerifier
{
    /// <summary>True for an https address under the repository's own release downloads on GitHub, and nowhere else.</summary>
    public static bool IsTrustedDownload(string? url, string repository)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
               && uri.IsDefaultPort
               && uri.UserInfo.Length == 0
               && uri.AbsolutePath.StartsWith($"/{repository}/releases/download/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The script run as root to install a downloaded .deb. The package sits in the user's own folder, where any program running as that
    /// user could swap it between the check and the install. So root copies it into a folder only root can write to, checks the checksum
    /// of that copy, and installs the copy. Arguments: the package, its SHA-256, and the folder for the copy (/var/tmp).
    /// </summary>
    private const string InstallScript = """
        set -e
        dir=$(mktemp -d "$3/yaparr-update.XXXXXX")
        trap 'rm -rf "$dir"' EXIT
        cp -- "$1" "$dir/package.deb"
        chmod 755 "$dir"
        chmod 644 "$dir/package.deb"
        echo "$2  $dir/package.deb" | sha256sum -c --quiet - >/dev/null 2>&1 || { echo "The package does not match its checksum." >&2; exit 3; }
        apt-get install -y --allow-downgrades "$dir/package.deb"
        """;

    /// <summary>
    /// What runs, detached, after the app has asked to be closed for an update on Linux: it waits until the app's process is gone (so no files are
    /// replaced under a running program), runs the installer command it was given, and then starts the app again, with the failure marked for it if
    /// the installer did not succeed. Arguments: the app's process id, the app's program, then the installer command and its arguments. It gives up
    /// without installing anything if the app is still running after thirty seconds.
    /// </summary>
    private const string UpdateRunnerScript = """
        pid=$1; exe=$2; shift 2
        n=0
        while kill -0 "$pid" 2>/dev/null; do
          n=$((n+1))
          [ "$n" -gt 300 ] && exit 4
          sleep 0.1
        done
        if ! "$@"; then YAPARR_UPDATE_FAILED=1; export YAPARR_UPDATE_FAILED; fi
        YAPARR_RESTARTED=1; export YAPARR_RESTARTED
        exec "$exe"
        """;

    public static string LinuxUpdateRunnerScript { get; } = UpdateRunnerScript.Replace("\r", "");

    /// <summary>
    /// What runs, detached, after the app has asked to be closed for an update on Windows: it runs the installer with the arguments it was given and waits for it,
    /// and then starts the app again, with the failure marked for it if the installer did not succeed (the same variables as on Linux). Without it an update
    /// would leave the person to find the app again, because a quiet install has no last page with a "Launch Yaparr" box. 0 and 3010 (done, a restart would
    /// finish it, which the installer is told not to do) are success. Parameters: the app's program, the installer's program, and the installer's arguments as
    /// one string.
    /// </summary>
    private const string WindowsRunnerScript = """
        param([string]$App, [string]$Installer, [string]$InstallerArguments)
        $failed = $true
        try {
          $p = Start-Process -FilePath $Installer -ArgumentList $InstallerArguments -Wait -PassThru
          $failed = ($p.ExitCode -ne 0 -and $p.ExitCode -ne 3010)
        } catch { $failed = $true }
        if ($failed) { $env:YAPARR_UPDATE_FAILED = '1' }
        $env:YAPARR_RESTARTED = '1'
        Start-Process -FilePath $App
        """;

    public static string WindowsUpdateRunnerScript { get; } = WindowsRunnerScript.Replace("\r\n", "\n").Replace("\n", "\r\n");

    /// <summary>The arguments for msiexec for a quiet update: a progress bar and no pages to click through, and no restart of the computer.</summary>
    public static string WindowsInstallerArguments(string package) => $"/passive /norestart /i \"{package}\"";

    /// <summary>The script as <c>sh</c> needs it: a source file checked out with Windows line endings must not put carriage returns into it.</summary>
    public static string LinuxInstallScript { get; } = InstallScript.Replace("\r", "");

    /// <summary>True for 64 hexadecimal digits, the form of a SHA-256 checksum.</summary>
    public static bool IsSha256(string? text) => text is { Length: 64 } && text.All(Uri.IsHexDigit);

    /// <summary>Checks a file against the published checksum. A file that does not match is deleted and an exception thrown.</summary>
    public static async Task VerifyAsync(string path, string expectedSha256, CancellationToken ct = default)
    {
        string actual;
        // The file has to be closed again before it can be deleted.
        await using (var file = File.OpenRead(path))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
        if (actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) return;
        File.Delete(path);
        throw new InvalidDataException("The downloaded installer failed its checksum, so it was not used.");
    }

    /// <summary>
    /// Checks a file against the published checksum and keeps it that way: it is opened so that no other program can write to it,
    /// delete it or rename it, and the checksum is taken through that same handle. What is checked is therefore what is read while
    /// the handle stays open. A file that does not match is deleted and an exception thrown. Dispose the result to let go.
    /// </summary>
    public static async Task<FileStream> OpenVerifiedAsync(string path, string expectedSha256, CancellationToken ct = default)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
            if (actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                file.Position = 0;
                return file;
            }
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        await file.DisposeAsync().ConfigureAwait(false);
        File.Delete(path);
        throw new InvalidDataException("The downloaded installer failed its checksum, so it was not used.");
    }
}
