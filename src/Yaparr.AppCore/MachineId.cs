using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Yaparr.AppCore;

/// <summary>
/// An id derived from the machine itself, for Yaparr Cloud's list of devices: the same machine signing in again after the app lost its own id (a profile that was wiped, the app
/// reinstalled) is then one entry, not two. It is the system's own id for this install (Windows: <c>MachineGuid</c>; Linux: <c>/etc/machine-id</c>; the phone app gives Android's
/// <c>ANDROID_ID</c>), and it is <b>never sent as it is</b>: what goes to a server is a hash of it mixed with that server's address, so two servers cannot tell that it is one machine,
/// and the id cannot be read back from it. The server uses it only together with the same device name, because machines cloned from one image share the id.
/// </summary>
public static class MachineId
{
    /// <summary>The system's id for this machine, or null when it cannot be read (an unsupported system, a sandbox that hides it).</summary>
    public static string? ReadLocal()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return ReadWindows();
            if (OperatingSystem.IsLinux())
                foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
                    if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: >= 16 } id) return id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or PlatformNotSupportedException) { }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadWindows()
    {
        using var hive = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
        using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid") as string is { Length: >= 16 } guid ? guid : null;
    }

    /// <summary>
    /// What this machine tells <paramref name="server"/>: 64 hex digits, a hash of the machine's id and the server's address (so it is different for each server, and the id cannot be
    /// got back from it). Null when there is no id. <paramref name="machineId"/> is for a platform that finds the id itself (the phone app); the default is <see cref="ReadLocal"/>.
    /// </summary>
    public static string? KeyFor(Uri server, string? machineId = null)
    {
        machineId ??= ReadLocal();
        if (machineId is not { Length: >= 16 }) return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("yaparr-machine-v1|" + server.Host.ToLowerInvariant() + "|" + machineId.Trim().ToLowerInvariant()));
        return Convert.ToHexStringLower(bytes);
    }
}
