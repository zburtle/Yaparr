namespace Yaparr.Screen;

/// <summary>
/// Whether this computer can share its screen, and how. Windows uses Graphics Capture; Linux under X11 copies the X display; Linux
/// under Wayland asks the desktop's ScreenCast portal (see <see cref="PortalScreenCast"/>).
/// </summary>
public static class ScreenCapture
{
    /// <summary>Null when a screen can be shared from here; otherwise a plain sentence saying why not.</summary>
    public static string? WhyNotSupported => Why(OperatingSystem.IsWindows(), OperatingSystem.IsLinux(),
        Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"), Environment.GetEnvironmentVariable("DISPLAY"));

    public static bool Supported => WhyNotSupported == null;

    /// <summary>
    /// Whether the share is chosen through the desktop's portal rather than from lists made here: a Wayland session, where a program
    /// may not copy the screen itself and cannot even list the windows. The desktop shows its own picker.
    /// </summary>
    public static bool UsesPortal => OperatingSystem.IsLinux()
        && IsWayland(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    /// <summary>
    /// Whether the share dialog on an X11 session can also offer "a window, chosen in the desktop's picker": the portal is there (GNOME and KDE have it on X11 too)
    /// and so is GStreamer. A window shared that way keeps being shared when it is resized or covered, which <c>x11grab</c> cannot do (it stops when the window gets
    /// smaller). Found out once, the first time it is asked.
    /// </summary>
    public static bool OffersPortalWindowOnX11 => _portalWindowOnX11.Value;

    private static readonly Lazy<bool> _portalWindowOnX11 = new(() =>
        OffersPortalWindowOnX11For(OperatingSystem.IsLinux(), UsesPortal, OperatingSystem.IsLinux() && PortalScreenCast.IsAvailable(), OperatingSystem.IsLinux() ? PortalPipeline.Missing() : "not Linux"));

    /// <summary>The decision, apart from the environment so that it can be checked on any system.</summary>
    public static bool OffersPortalWindowOnX11For(bool linux, bool wayland, bool portalAvailable, string? gstreamerMissing)
        => linux && !wayland && portalAvailable && gstreamerMissing == null;

    public static bool IsWayland(string? sessionType, string? waylandDisplay) =>
        string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase)
        || (string.IsNullOrEmpty(sessionType) && !string.IsNullOrEmpty(waylandDisplay));

    /// <summary>
    /// The decision, apart from the environment so that it can be checked on any system. Under Wayland sharing is offered; whether
    /// the portal and GStreamer are there is found out when it starts, with a message saying what to install.
    /// </summary>
    public static string? Why(bool windows, bool linux, string? sessionType, string? waylandDisplay, string? display)
    {
        if (windows) return null;
        if (!linux) return "Sharing a screen works on Windows and Linux only for now.";
        if (IsWayland(sessionType, waylandDisplay)) return null;
        if (string.IsNullOrEmpty(display)) return "There is no X11 display to share (the DISPLAY variable is not set).";
        return null;
    }
}

/// <summary>Whether the sound of a shared screen can be shared too: per program on Windows 10 2004 or newer, and on Linux where the sound server's tools are installed.</summary>
public static class ScreenSound
{
    public static bool IsSupported => ProcessLoopback.IsSupported || PulseAppCapture.IsSupported;
}
