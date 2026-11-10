using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;

namespace Yaparr.Desktop;

// Closing the window with X keeps Yaparr running (calls, and messages arriving) behind an icon in the notification area, unless the person turned that off.
// The icon opens the window again; its Exit really quits.
public partial class MainWindow
{
    private TrayIcon? _tray;
    private bool _quitting;

    // Windows tells a program when an installer, or the end of the session, wants it closed (WM_QUERYENDSESSION, which the Restart Manager that
    // installers use sends). Avalonia answers "yes" but leaves the program running, so one hidden in the notification area would still be there
    // when the installer replaces its files. So that message is taken as a real quit, not as a click on X.
    private void QuitWhenWindowsAsksToEndTheSession()
    {
        if (!OperatingSystem.IsWindows()) return;
        const uint WM_QUERYENDSESSION = 0x0011;
        Avalonia.Controls.Win32Properties.AddWndProcHookCallback(this, (IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WM_QUERYENDSESSION)
            {
                _quitting = true;
                Avalonia.Threading.Dispatcher.UIThread.Post(Quit);
            }
            return IntPtr.Zero;
        });
    }

    private void SetUpTray()
    {
        QuitWhenWindowsAsksToEndTheSession();
        CloseToTrayItem.IsChecked = _settings.CloseToTray;
        CloseToTrayItem.PropertyChanged += (_, e) =>
        {
            if (e.Property != Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty) return;
            _settings.CloseToTray = CloseToTrayItem.IsChecked == true;
            _data.SaveSettings(_settings);
            if (_tray != null) _tray.IsVisible = _settings.CloseToTray;
        };

        try
        {
            var open = new NativeMenuItem("Open Yaparr");
            open.Click += (_, _) => ShowFromTray();
            var exit = new NativeMenuItem("Exit");
            exit.Click += (_, _) => Quit();
            _tray = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Yaparr.Desktop/Assets/yaparr.png"))),
                ToolTipText = "Yaparr",
                Menu = new NativeMenu { open, exit },
                IsVisible = _settings.CloseToTray,
            };
            _tray.Clicked += (_, _) => ShowFromTray();
            TrayIcon.SetIcons(Application.Current!, new TrayIcons { _tray });
        }
        catch (Exception ex)
        {
            // No notification area to put an icon in: the window then closes as it always did.
            _tray = null;
            _log.Write("tray icon: " + ex.Message);
        }
    }

    // True when the click on X should keep the program running instead of ending it.
    private bool KeepRunningOnClose(WindowClosingEventArgs e) =>
        e.CloseReason == WindowCloseReason.WindowClosing && !_quitting && _settings.CloseToTray && _tray != null;

    private void HideToTray()
    {
        _log.Write("closed to the notification area");
        // Where a desktop has no notification area by default (GNOME), a hidden window could not be brought back, so there it goes to the taskbar instead.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) Hide();
        else WindowState = WindowState.Minimized;
    }

    private void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void Quit()
    {
        _quitting = true;
        Close();
    }
}
