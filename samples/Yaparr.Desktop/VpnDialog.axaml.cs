using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yaparr.Chat;
using Yaparr.Vpn;

namespace Yaparr.Desktop;

public partial class VpnDialog : Window
{
    private readonly VpnController _vpn = null!;
    private bool _busy;

    public VpnDialog() => InitializeComponent();

    public VpnDialog(VpnController vpn) : this()
    {
        _vpn = vpn;
        Refresh();
        if (vpn.Node is { } node)
        {
            Action<VpnState> onState = _ => Dispatcher.UIThread.Post(Refresh);
            Action<NetworkStatus> onNetwork = _ => Dispatcher.UIThread.Post(Refresh);
            node.VpnStateChanged += onState;
            node.NetworkStatusChanged += onNetwork;
            Closed += (_, _) => { node.VpnStateChanged -= onState; node.NetworkStatusChanged -= onNetwork; };
        }
    }

    private void Refresh()
    {
        var config = _vpn.LoadConfig();
        var state = _vpn.Node?.VpnState ?? VpnState.Disconnected;

        ConfigText.Text = config == null
            ? (_vpn.HasConfig ? "The saved configuration could not be read. Import it again." : "No configuration imported.")
            : $"Server: {config.Endpoint}   ·   Your VPN address: {config.Address}";
        StatusText.Text = state switch
        {
            VpnState.Connected => "Connected. Yaparr traffic is going through the VPN.",
            VpnState.Connecting => "Connecting…",
            _ => "Not connected.",
        };
        NetworkText.Text = state == VpnState.Connected && _vpn.Node?.NetworkStatus is { } status && status != NetworkStatus.Unknown
            ? "What peers see: " + status.Nat.Describe()
            : "";

        ToggleButton.Content = state == VpnState.Disconnected ? "Connect" : "Disconnect";
        ToggleButton.IsEnabled = !_busy && config != null;
        ImportButton.IsEnabled = !_busy;
        RemoveButton.IsEnabled = !_busy && _vpn.HasConfig;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private async void Import_Click(object? sender, RoutedEventArgs e)
    {
        var path = await Dialogs.PickOpenAsync(this, "Import WireGuard configuration", Dialogs.WireGuard, Dialogs.AllFiles);
        if (path == null) return;
        try { _vpn.Import(path); }
        catch (Exception ex) when (ex is FormatException or System.IO.IOException or UnauthorizedAccessException)
        {
            await Dialogs.MessageAsync(this, "Import WireGuard configuration", ex.Message);
        }
        Refresh();
    }

    private async void Toggle_Click(object? sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            if ((_vpn.Node?.VpnState ?? VpnState.Disconnected) == VpnState.Disconnected)
            {
                StatusText.Text = "Connecting…";
                if (!await _vpn.ConnectAsync())
                    await Dialogs.MessageAsync(this, "VPN",
                        "The VPN server did not answer. Yaparr will keep trying; check the server address and keys in the configuration.");
            }
            else await _vpn.DisconnectAsync();
        });
    }

    private async void Remove_Click(object? sender, RoutedEventArgs e)
    {
        if (!await Dialogs.ConfirmAsync(this, "VPN", "Remove the saved VPN configuration?")) return;
        await RunAsync(_vpn.RemoveAsync);
    }

    private async Task RunAsync(Func<Task> action)
    {
        _busy = true;
        Refresh();
        try { await action(); }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException or FormatException)
        {
            await Dialogs.MessageAsync(this, "VPN", ex.Message);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }
}
