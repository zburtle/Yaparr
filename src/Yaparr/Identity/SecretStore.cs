using System.Security.Cryptography;
using System.Text;

namespace Yaparr.Identity;

/// <summary>
/// Keeps files with keys in them (the identity, private room keys, a VPN configuration) from being read by anything
/// that is not you. On Windows the contents are sealed with DPAPI, so only the same Windows user on the same computer
/// can open them: a stolen copy of the folder, a backup of it or another account on the machine gets nothing. On
/// Linux and macOS there is no equivalent that works without a password, so the files stay as they are but can only
/// be read by their owner.
/// Files written by earlier versions are plain and are still read; <see cref="Upgrade"/> seals them.
/// </summary>
public static class SecretStore
{
    private static readonly byte[] Magic = "YPSEC1"u8.ToArray();
    private static readonly byte[] Entropy = "Yaparr secret file"u8.ToArray();
    private const string TextPrefix = "dpapi:";

    public static bool IsSealed(ReadOnlySpan<byte> data) => data.StartsWith(Magic);

    public static byte[] Seal(byte[] plain)
    {
        if (!OperatingSystem.IsWindows()) return plain;
        var blob = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        return [.. Magic, .. blob];
    }

    /// <summary>The contents of a file written by <see cref="Seal"/> or of a plain one. Throws <see cref="CryptographicException"/> for a sealed file this user cannot open.</summary>
    public static byte[] Open(byte[] data)
    {
        if (!IsSealed(data)) return data;
        if (!OperatingSystem.IsWindows()) throw new CryptographicException("This file was protected by Windows and cannot be opened here. Restore it from a profile export.");
        return ProtectedData.Unprotect(data[Magic.Length..], Entropy, DataProtectionScope.CurrentUser);
    }

    public static byte[] ReadAllBytes(string path) => Open(File.ReadAllBytes(path));

    public static string ReadAllText(string path) => Encoding.UTF8.GetString(ReadAllBytes(path));

    /// <summary>Writes the file whole or not at all, closed to other users.</summary>
    public static void WriteAllBytes(string path, byte[] plain)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options)) stream.Write(Seal(plain));
        File.Move(temp, path, overwrite: true);
    }

    public static void WriteAllText(string path, string text) => WriteAllBytes(path, Encoding.UTF8.GetBytes(text));

    /// <summary>Seals a file that an earlier version left plain. Returns false if there was nothing to do.</summary>
    public static bool Upgrade(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return false;
        var data = File.ReadAllBytes(path);
        if (IsSealed(data)) return false;
        WriteAllBytes(path, data);
        return true;
    }

    /// <summary>A short secret kept inside a text file that people may also edit by hand.</summary>
    public static string SealText(string plain) =>
        OperatingSystem.IsWindows() && plain.Length > 0 ? TextPrefix + Convert.ToBase64String(Seal(Encoding.UTF8.GetBytes(plain))) : plain;

    /// <summary>The text from <see cref="SealText"/>, or the text itself if it was never sealed. Empty if it cannot be opened by this user.</summary>
    public static string OpenText(string stored)
    {
        if (!stored.StartsWith(TextPrefix, StringComparison.Ordinal)) return stored;
        try { return Encoding.UTF8.GetString(Open(Convert.FromBase64String(stored[TextPrefix.Length..]))); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return ""; }
    }
}
