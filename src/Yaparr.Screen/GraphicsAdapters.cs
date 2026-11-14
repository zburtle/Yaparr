using System.Globalization;
using System.Text.RegularExpressions;

namespace Yaparr.Screen;

/// <summary>Whose graphics hardware an adapter is, by its PCI vendor id.</summary>
public enum GpuVendor { Nvidia, Amd, Intel, Other }

/// <summary>
/// The graphics adapters this computer has, which is what tells which graphics card encoders can possibly work (issue #55). Auto used to try each encoder
/// in turn, and one that cannot start costs about half a second (about a second on a computer with only Intel graphics): NVIDIA's on a computer with no
/// NVIDIA card, AMD's with no AMD card. Now an encoder whose maker has no adapter here is left out. It is only ever a shortcut: when the adapters cannot be
/// listed (no permission, an unknown system) nothing is left out, and so is it when YAPARR_GPU_CHECK is 0; an encoder chosen on purpose is always tried.
/// </summary>
public static class GraphicsAdapters
{
    /// <summary>The PCI vendor ids of the makers whose encoders Yaparr drives.</summary>
    private const uint NvidiaId = 0x10DE, AmdId = 0x1002, IntelId = 0x8086;

    /// <summary>The vendor of a PCI vendor id.</summary>
    public static GpuVendor VendorOf(uint pciVendorId) => pciVendorId switch
    {
        NvidiaId => GpuVendor.Nvidia,
        AmdId => GpuVendor.Amd,
        IntelId => GpuVendor.Intel,
        _ => GpuVendor.Other,
    };

    /// <summary>The vendor id in a Windows display device's matching id ("pci\ven_10de&amp;dev_2684&amp;subsys_..."), or null when it has none (a basic or remote display adapter).</summary>
    public static uint? ParseWindowsDeviceId(string? matchingDeviceId)
    {
        if (matchingDeviceId == null) return null;
        var m = Regex.Match(matchingDeviceId, @"ven_([0-9a-f]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success && uint.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    /// <summary>The vendor id in the text of a /sys/class/drm/card*/device/vendor file ("0x10de").</summary>
    public static uint? ParseSysfsVendor(string? text)
    {
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    /// <summary>
    /// Whether an encoder can have hardware to run on among these vendors. NVIDIA's needs an NVIDIA adapter, AMD's an AMD one, Quick Sync an Intel one; VA-API
    /// drives Intel and AMD, and any maker it does not know. True when the vendors are not known (null or empty): then it is simply tried.
    /// </summary>
    public static bool CanHave(HardwareEncoder encoder, IReadOnlyCollection<GpuVendor>? vendors)
    {
        if (vendors == null || vendors.Count == 0) return true;
        return encoder switch
        {
            HardwareEncoder.Nvenc => vendors.Contains(GpuVendor.Nvidia),
            HardwareEncoder.Amf => vendors.Contains(GpuVendor.Amd),
            HardwareEncoder.Qsv => vendors.Contains(GpuVendor.Intel),
            HardwareEncoder.Vaapi => vendors.Contains(GpuVendor.Intel) || vendors.Contains(GpuVendor.Amd) || vendors.Contains(GpuVendor.Other),
            _ => true,
        };
    }

    /// <summary>Where Windows keeps the display adapters (the device class {4d36e968-e325-11ce-bfc1-08002be10318}, whose `Class` value is `Display`).</summary>
    internal const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static readonly Lazy<IReadOnlyList<GpuVendor>?> Found = new(Detect);

    /// <summary>The makers of the adapters in this computer, or null when that cannot be told (or the check is turned off with YAPARR_GPU_CHECK=0).</summary>
    public static IReadOnlyList<GpuVendor>? Vendors => Environment.GetEnvironmentVariable("YAPARR_GPU_CHECK") == "0" ? null : Found.Value;

    private static IReadOnlyList<GpuVendor>? Detect()
    {
        try
        {
            var ids = OperatingSystem.IsWindows() ? WindowsVendorIds() : OperatingSystem.IsLinux() ? LinuxVendorIds() : new List<uint>();
            return ids.Count == 0 ? null : ids.Select(VendorOf).Distinct().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            return null;
        }
    }

    // The display adapters Windows has drivers for, with outputs or not (a laptop's second graphics chip has none and is not listed by what is attached to the desktop).
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<uint> WindowsVendorIds()
    {
        var ids = new List<uint>();
        using var classKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(DisplayClassKey);
        if (classKey == null) return ids;
        foreach (var name in classKey.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            using var adapter = classKey.OpenSubKey(name);
            if (adapter?.GetValue("MatchingDeviceId") is string id && ParseWindowsDeviceId(id) is { } vendor) ids.Add(vendor);
        }
        return ids;
    }

    private static List<uint> LinuxVendorIds()
    {
        var ids = new List<uint>();
        if (!Directory.Exists("/sys/class/drm")) return ids;
        foreach (var card in Directory.GetDirectories("/sys/class/drm").Where(d => Regex.IsMatch(Path.GetFileName(d), @"^card\d+$")))
        {
            var file = Path.Combine(card, "device", "vendor");
            if (File.Exists(file) && ParseSysfsVendor(File.ReadAllText(file)) is { } vendor) ids.Add(vendor);
        }
        return ids;
    }

    /// <summary>The encoders worth trying on this computer, in the order Auto tries them: <see cref="HardwareEncoders.InOrder"/> without those whose maker has no adapter here.</summary>
    public static IReadOnlyList<HardwareEncoder> Installed(IReadOnlyList<GpuVendor>? vendors) =>
        HardwareEncoders.InOrder.Where(e => CanHave(e, vendors)).ToList();

    /// <summary>A line for the diagnostics: the adapters found, the encoders that will be tried and the ones left out.</summary>
    public static string Describe() => Describe(Vendors);

    /// <summary>The same for the given adapters (null: not known).</summary>
    public static string Describe(IReadOnlyList<GpuVendor>? vendors)
    {
        if (vendors == null) return "not known" + (Environment.GetEnvironmentVariable("YAPARR_GPU_CHECK") == "0" ? " (the check is turned off with YAPARR_GPU_CHECK=0)" : "; every graphics card encoder is tried in turn");
        var left = HardwareEncoders.InOrder.Where(e => !CanHave(e, vendors)).Select(e => HardwareEncoders.Name(e)).ToList();
        return string.Join(", ", vendors) + (left.Count > 0 ? "; left out as there is no such adapter: " + string.Join(", ", left) : "; every encoder has an adapter");
    }
}
