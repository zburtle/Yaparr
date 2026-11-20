using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Yaparr.AppCore;

namespace Yaparr.Desktop;

/// <summary>
/// What the signed-in Yaparr Cloud account holds against its limits (message history and shelves, pictures left for people who are away, backups, today's gateway allowance), and the
/// devices signed in as it, each with a button to sign it out (see <see cref="CloudAccountClient"/>). Opened from the Yaparr Cloud section of the settings.
/// </summary>
public sealed class CloudAccountWindow : Window
{
    private readonly Uri _server;
    private readonly string _token;
    private readonly StackPanel _usage = new() { Spacing = 10 };
    private readonly StackPanel _devices = new() { Spacing = 6 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray };
    private readonly Action? _signedOut;
    private readonly Button _signOutOthers;
    private IReadOnlyList<CloudDevice> _listed = new List<CloudDevice>();
    private bool _busy;

    public CloudAccountWindow(Uri server, string token, Action? signedOut = null)
    {
        _server = server;
        _token = token;
        _signedOut = signedOut;
        Title = "Yaparr Cloud account";
        Width = 520;
        Height = 560;
        MinHeight = 360;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _signOutOthers = new Button { Content = "Sign out all the others", HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
        _signOutOthers.Click += async (_, _) => await SignOutOthersAsync();
        var refresh = new Button { Content = "Refresh", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        refresh.Click += async (_, _) => await LoadAsync();
        var close = new Button { Content = "Close", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { refresh, close } };

        var body = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "What your account holds", FontSize = 15, FontWeight = FontWeight.SemiBold },
                _usage,
                new TextBlock { Text = "Devices signed in", FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 0) },
                _devices,
                _signOutOthers,
                _status,
            },
        };
        Content = new DockPanel
        {
            Margin = new Thickness(16),
            LastChildFill = true,
            Children =
            {
                Dock(buttons, Avalonia.Controls.Dock.Bottom),
                new ScrollViewer { Content = body, Padding = new Thickness(0, 0, 12, 0) },
            },
        };
        Opened += async (_, _) => await LoadAsync();
    }

    private static Control Dock(Control control, Dock dock)
    {
        control.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(control, dock);
        return control;
    }

    private async Task LoadAsync()
    {
        if (_busy) return;
        _busy = true;
        _status.Text = "Asking Yaparr Cloud…";
        try
        {
            using var client = new CloudAccountClient(_server, _token);
            var usage = await client.UsageAsync();
            var devices = await client.DevicesAsync();
            ShowUsage(usage);
            ShowDevices(devices);
            _status.Text = "";
        }
        catch (BackupServerException ex) { Failed(ex); }
        finally { _busy = false; }
    }

    private void Failed(BackupServerException ex)
    {
        _status.Text = ex.SignedOut ? "This device is no longer signed in to Yaparr Cloud. Sign in again from the settings." : ex.Message;
        if (ex.SignedOut) _signedOut?.Invoke();
    }

    private void ShowUsage(CloudUsage usage)
    {
        _usage.Children.Clear();
        foreach (var line in CloudUsageText.Lines(usage))
        {
            var panel = new StackPanel { Spacing = 3 };
            panel.Children.Add(new TextBlock { Text = line.Label, FontWeight = FontWeight.SemiBold });
            if (line.Fraction is { } fraction)
                panel.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = fraction, Height = 6, Foreground = line.Warn ? Brushes.DarkOrange : Brushes.SeaGreen });
            panel.Children.Add(new TextBlock { Text = line.Value, TextWrapping = TextWrapping.Wrap, Foreground = line.Warn ? Brushes.DarkOrange : Brushes.Gray });
            _usage.Children.Add(panel);
        }
    }

    private void ShowDevices(IReadOnlyList<CloudDevice> devices)
    {
        _listed = devices;
        _signOutOthers.IsVisible = devices.Count(d => !d.Current) >= 2;
        _devices.Children.Clear();
        if (devices.Count == 0) _devices.Children.Add(new TextBlock { Text = "None.", Foreground = Brushes.Gray });
        foreach (var device in devices)
        {
            var row = new DockPanel { LastChildFill = true };
            var signOut = new Button { Content = device.Current ? "Sign out here" : "Sign out", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            DockPanel.SetDock(signOut, Avalonia.Controls.Dock.Right);
            var id = device.Id;
            signOut.Click += async (_, _) => await SignOutAsync(id, device.Current);
            row.Children.Add(signOut);
            row.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = device.Name + (device.Current ? " (this device)" : ""), FontWeight = device.Current ? FontWeight.SemiBold : FontWeight.Normal },
                    new TextBlock { Text = "Signed in " + device.Created.ToLocalTime().ToString("d MMM yyyy"), Foreground = Brushes.Gray, FontSize = 12 },
                },
            });
            _devices.Children.Add(row);
        }
    }

    // Old sign-ins pile up (a reinstall, a sign-in again): all of them but this one, at once.
    private async Task SignOutOthersAsync()
    {
        if (_busy) return;
        var others = _listed.Where(d => !d.Current).ToList();
        if (others.Count == 0) return;
        if (!await Dialogs.ConfirmAsync(this, "Sign out the others",
                $"Sign {others.Count} other devices out of Yaparr Cloud? Each stops syncing until it signs in again. Nothing on them is deleted, and this device stays signed in.", "Sign them out", "Cancel")) return;
        _busy = true;
        try
        {
            using var client = new CloudAccountClient(_server, _token);
            foreach (var device in others) await client.RevokeDeviceAsync(device.Id);
        }
        catch (BackupServerException ex) { Failed(ex); _busy = false; return; }
        _busy = false;
        await LoadAsync();
    }

    private async Task SignOutAsync(string id, bool current)
    {
        if (_busy) return;
        if (!await Dialogs.ConfirmAsync(this, "Sign out", current
                ? "Sign this device out of Yaparr Cloud? It stops syncing until you sign in again."
                : "Sign that device out of Yaparr Cloud? It stops syncing until it signs in again. Nothing on it is deleted.", "Sign out", "Cancel")) return;
        _busy = true;
        try
        {
            using var client = new CloudAccountClient(_server, _token);
            await client.RevokeDeviceAsync(id);
        }
        catch (BackupServerException ex) { Failed(ex); _busy = false; return; }
        _busy = false;
        if (current) { _signedOut?.Invoke(); Close(); return; }
        await LoadAsync();
    }
}
