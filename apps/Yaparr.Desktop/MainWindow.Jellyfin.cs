using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaparr.AppCore;

namespace Yaparr.Desktop;

// The Jellyfin section of the settings: finding the server, signing in (Quick Connect, or a name and password) and signing out. What is in the library
// is looked at in JellyfinWindow. Everything here talks to the person's own server from this computer.
public partial class MainWindow
{
    private JellyfinState _jellyfin = new();
    private (Uri Address, JellyfinServerInfo Info)? _jellyfinFound;
    private CancellationTokenSource? _jellyfinQuick;
    private bool _jellyfinBusy;

    private void ShowJellyfinState()
    {
        _jellyfin = _data.LoadJellyfin();
        JellyfinSignedOutPanel.IsVisible = !_jellyfin.SignedIn;
        JellyfinSignedInPanel.IsVisible = _jellyfin.SignedIn;
        if (_jellyfin.SignedIn)
        {
            var host = Uri.TryCreate(_jellyfin.Url, UriKind.Absolute, out var uri) ? uri.Authority : _jellyfin.Url;
            JellyfinSignedInText.Text = $"Signed in to {(_jellyfin.ServerName.Length > 0 ? _jellyfin.ServerName + " (" + host + ")" : host)} as {_jellyfin.UserName}.";
        }
        else if (JellyfinAddressBox.Text is null or "" && _jellyfin.Url.Length > 0) JellyfinAddressBox.Text = _jellyfin.Url;
        JellyfinStatus.IsVisible = false;
    }

    private void StopJellyfinQuick()
    {
        _jellyfinQuick?.Cancel();
        _jellyfinQuick = null;
        JellyfinQuickText.IsVisible = false;
        JellyfinQuickButton.IsEnabled = true;
    }

    private void JellyfinSay(string text, bool bad = false)
    {
        JellyfinStatus.Text = text;
        JellyfinStatus.Foreground = StatusBrush(bad ? "Bad" : "Muted");
        JellyfinStatus.IsVisible = text.Length > 0;
    }

    private string JellyfinDeviceId()
    {
        if (_jellyfin.DeviceId.Length == 0) _jellyfin.DeviceId = Guid.NewGuid().ToString("N");
        return _jellyfin.DeviceId;
    }

    private void JellyfinAddress_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) JellyfinFind_Click(sender, e);
    }

    private void JellyfinPassword_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) JellyfinSignIn_Click(sender, e);
    }

    private async void JellyfinFind_Click(object? sender, RoutedEventArgs e)
    {
        if (_jellyfinBusy) return;
        var typed = JellyfinAddressBox.Text ?? "";
        if (JellyfinClient.AddressesFor(typed).Count == 0) { JellyfinSay("Type the address of your Jellyfin server, for example 192.168.1.20:8096.", bad: true); return; }
        _jellyfinBusy = true;
        JellyfinFindButton.IsEnabled = false;
        StopJellyfinQuick();
        JellyfinSignInPanel.IsVisible = false;
        JellyfinSay("Looking for the server…");
        try
        {
            var found = await JellyfinClient.FindServerAsync(typed, JellyfinDeviceId());
            if (found is not { } server)
            {
                JellyfinSay("No Jellyfin server answered at that address (https and http were tried). Check the address, and that the server is on.", bad: true);
                return;
            }
            _jellyfinFound = server;
            JellyfinServerText.Text = $"Found {(server.Info.Name.Length > 0 ? server.Info.Name : "a Jellyfin server")} (version {server.Info.Version}) at {server.Address.Authority}.";
            using (var client = new JellyfinClient(server.Address, JellyfinDeviceId()))
                JellyfinQuickPanel.IsVisible = await client.QuickConnectEnabledAsync();
            JellyfinSignInPanel.IsVisible = true;
            JellyfinSay("");
        }
        finally
        {
            _jellyfinBusy = false;
            JellyfinFindButton.IsEnabled = true;
        }
    }

    private async void JellyfinQuick_Click(object? sender, RoutedEventArgs e)
    {
        if (_jellyfinFound is not { } server || _jellyfinBusy) return;
        StopJellyfinQuick();
        var cts = _jellyfinQuick = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        JellyfinQuickButton.IsEnabled = false;
        try
        {
            using var client = new JellyfinClient(server.Address, JellyfinDeviceId());
            var ticket = await client.StartQuickConnectAsync(cts.Token);
            JellyfinQuickText.Text = $"In Jellyfin, open your profile, choose Quick Connect and enter {ticket.Code}. Waiting for you…";
            JellyfinQuickText.IsVisible = true;
            JellyfinSay("");
            while (!await client.QuickConnectApprovedAsync(ticket.Secret, cts.Token))
                await Task.Delay(TimeSpan.FromSeconds(2.5), cts.Token);
            var login = await client.SignInWithQuickConnectAsync(ticket.Secret, cts.Token);
            FinishJellyfinSignIn(server, login);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_jellyfinQuick, cts)) JellyfinSay("Quick Connect timed out. Press the button to try again.", bad: true);
        }
        catch (Exception ex) when (ex is JellyfinException or HttpRequestException)
        {
            JellyfinSay("Quick Connect did not work: " + ex.Message, bad: true);
        }
        finally
        {
            if (ReferenceEquals(_jellyfinQuick, cts)) StopJellyfinQuick();
        }
    }

    private async void JellyfinSignIn_Click(object? sender, RoutedEventArgs e)
    {
        if (_jellyfinFound is not { } server || _jellyfinBusy) return;
        var user = (JellyfinUserBox.Text ?? "").Trim();
        var password = JellyfinPasswordBox.Text ?? "";
        if (user.Length == 0) { JellyfinSay("Type your Jellyfin user name.", bad: true); return; }
        _jellyfinBusy = true;
        JellyfinSignInButton.IsEnabled = false;
        JellyfinSay("Signing in…");
        try
        {
            using var client = new JellyfinClient(server.Address, JellyfinDeviceId());
            var login = await client.SignInAsync(user, password);
            JellyfinPasswordBox.Text = "";
            FinishJellyfinSignIn(server, login);
        }
        catch (Exception ex) when (ex is JellyfinException or HttpRequestException or TaskCanceledException)
        {
            JellyfinSay(ex is HttpRequestException or TaskCanceledException ? "The server could not be reached." : ex.Message, bad: true);
        }
        finally
        {
            _jellyfinBusy = false;
            JellyfinSignInButton.IsEnabled = true;
        }
    }

    private void FinishJellyfinSignIn((Uri Address, JellyfinServerInfo Info) server, JellyfinLogin login)
    {
        _jellyfin = new JellyfinState
        {
            Url = server.Address.ToString(), ServerName = server.Info.Name, UserId = login.UserId, UserName = login.UserName, Token = login.Token, DeviceId = JellyfinDeviceId(),
        };
        _data.SaveJellyfin(_jellyfin);
        _jellyfinFound = null;
        JellyfinSignInPanel.IsVisible = false;
        ShowJellyfinState();
    }

    private async void JellyfinSignOut_Click(object? sender, RoutedEventArgs e)
    {
        if (_jellyfinBusy || !_jellyfin.SignedIn) return;
        _jellyfinBusy = true;
        try
        {
            if (Uri.TryCreate(_jellyfin.Url, UriKind.Absolute, out var uri))
            {
                using var client = new JellyfinClient(uri, JellyfinDeviceId(), _jellyfin.Token, _jellyfin.UserId);
                await client.SignOutAsync();
            }
            ForgetJellyfinSignIn();
        }
        finally { _jellyfinBusy = false; }
    }

    // The token is gone (signed out, or the server stopped accepting it); the server and the device's name are kept, so signing in again is quick.
    private void ForgetJellyfinSignIn()
    {
        _jellyfin.Token = "";
        _jellyfin.UserId = "";
        _jellyfin.UserName = "";
        _data.SaveJellyfin(_jellyfin);
        ShowJellyfinState();
    }

    private void JellyfinBrowse_Click(object? sender, RoutedEventArgs e)
    {
        if (!_jellyfin.SignedIn || !Uri.TryCreate(_jellyfin.Url, UriKind.Absolute, out var uri)) return;
        var client = new JellyfinClient(uri, JellyfinDeviceId(), _jellyfin.Token, _jellyfin.UserId);
        var window = new JellyfinWindow(client, _jellyfin.ServerName, () => UI(() =>
        {
            // The server no longer accepts the sign-in.
            ForgetJellyfinSignIn();
            JellyfinSay("Jellyfin signed you out. Sign in again to browse.", bad: true);
        }));
        window.Show(this);
    }
}
