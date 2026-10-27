using System.Windows;
using Yaparr.Chat;
using Yaparr.Vpn;

namespace Yaparr.Wpf;

public partial class VpnDialog : Window
{
    private readonly VpnController _vpn;
    private bool _busy;

    public VpnDialog(VpnController vpn)
    {
        _vpn = vpn;
        InitializeComponent();
        Refresh();
        if (vpn.Node is { } node)
        {
            Action<VpnState> onState = _ => Dispatcher.BeginInvoke(Refresh);
            Action<NetworkStatus> onNetwork = _ => Dispatcher.BeginInvoke(Refresh);
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

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import WireGuard configuration",
            Filter = "WireGuard configuration (*.conf)|*.conf|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        try { _vpn.Import(dialog.FileName); }
        catch (Exception ex) when (ex is FormatException or System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Import WireGuard configuration", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Refresh();
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            if ((_vpn.Node?.VpnState ?? VpnState.Disconnected) == VpnState.Disconnected)
            {
                StatusText.Text = "Connecting…";
                if (!await _vpn.ConnectAsync())
                    MessageBox.Show(this, "The VPN server did not answer. Yaparr will keep trying; check the server address and keys in the configuration.",
                        "VPN", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else await _vpn.DisconnectAsync();
        });
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Remove the saved VPN configuration?", "VPN", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync(_vpn.RemoveAsync);
    }

    private async Task RunAsync(Func<Task> action)
    {
        _busy = true;
        Refresh();
        try { await action(); }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException or FormatException)
        {
            MessageBox.Show(this, ex.Message, "VPN", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }
}
