using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SkiaSharp;

namespace Yaparr.Desktop;

/// <summary>
/// Puts the number of unread messages on the program's button in the Windows taskbar, as a small red badge. Other systems have their own ways
/// to show this and are not covered.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TaskbarBadge
{
    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private class TaskbarList { }

    // Only SetOverlayIcon is used; the methods before it are there to put it in the right place in the interface.
    [ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr hwnd);
        [PreserveSig] int DeleteTab(IntPtr hwnd);
        [PreserveSig] int ActivateTab(IntPtr hwnd);
        [PreserveSig] int SetActiveAlt(IntPtr hwnd);
        [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        [PreserveSig] int SetProgressState(IntPtr hwnd, int flags);
        [PreserveSig] int RegisterTab(IntPtr hwndTab, IntPtr hwndMdi);
        [PreserveSig] int UnregisterTab(IntPtr hwndTab);
        [PreserveSig] int SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        [PreserveSig] int SetTabActive(IntPtr hwndTab, IntPtr hwndMdi, uint reserved);
        [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint count, IntPtr buttons);
        [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint count, IntPtr buttons);
        [PreserveSig] int ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);
        [PreserveSig] int SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint size, bool icon, uint version, int width, int height, uint flags);

    private static ITaskbarList3? _list;
    private static readonly Dictionary<string, IntPtr> Icons = new();
    private static int _shown = -1;

    /// <summary>Shows <paramref name="count"/> on the taskbar button of the window, or nothing for zero. Returns false if Windows would not.</summary>
    public static bool Set(IntPtr window, int count)
    {
        count = Math.Max(0, count);
        if (count == _shown) return true;
        try
        {
            if (_list == null)
            {
                _list = (ITaskbarList3)new TaskbarList();
                _list.HrInit();
            }
            IntPtr icon = count == 0 ? IntPtr.Zero : IconFor(count);
            if (count > 0 && icon == IntPtr.Zero) return false;
            int result = _list.SetOverlayIcon(window, icon, count == 0 ? null : count == 1 ? "1 unread message" : $"{count} unread messages");
            if (result < 0) return false;
            _shown = count;
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static IntPtr IconFor(int count)
    {
        string text = count > 9 ? "9+" : count.ToString();
        if (Icons.TryGetValue(text, out var known)) return known;
        var icon = CreateIcon(text);
        Icons[text] = icon;
        return icon;
    }

    // A red disc with the number in white, drawn at the size the taskbar uses for badges.
    private static IntPtr CreateIcon(string text)
    {
        const int size = 32;
        using var bitmap = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var fill = new SKPaint { Color = new SKColor(0xD9, 0x3B, 0x3B), IsAntialias = true };
            canvas.DrawCircle(size / 2f, size / 2f, size / 2f - 1, fill);
            using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), text.Length > 1 ? 17 : 21);
            using var ink = new SKPaint { Color = SKColors.White, IsAntialias = true };
            var width = font.MeasureText(text);
            canvas.DrawText(text, (size - width) / 2f, size / 2f + font.Size * 0.36f, SKTextAlign.Left, font, ink);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = png.ToArray();
        // A PNG is accepted as the bits of an icon.
        return CreateIconFromResourceEx(bytes, (uint)bytes.Length, true, 0x00030000, size, size, 0);
    }
}
