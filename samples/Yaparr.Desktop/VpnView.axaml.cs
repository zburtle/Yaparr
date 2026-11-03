using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yaparr.Chat;
using Yaparr.Vpn;

namespace Yaparr.Desktop;

/// <summary>The VPN, shown in the settings under Network. It follows the VPN's state while it is on screen.</summary>
public partial class VpnView : UserControl
{
    private VpnController? _vpn;
    private ChatNode? _watching;
    private bool _busy;
    private readonly Action<VpnState> _onState;
    private readonly Action<NetworkStatus> _onNetwork;

    public VpnView()
    {
        InitializeComponent();
        _onState = _ => Dispatcher.UIThread.Post(Refresh);
        _onNetwork = _ => Dispatcher.UIThread.Post(Refresh);
    }

    internal void Attach(VpnController vpn) => _vpn = vpn;

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    /// <summary>Called when the section is shown or put away: shows the current state and follows it only while on screen.</summary>
    internal void SetActive(bool active)
    {
        if (_watching != null)
        {
            _watching.VpnStateChanged -= _onState;
            _watching.NetworkStatusChanged -= _onNetwork;
            _watching = null;
        }
        if (!active || _vpn == null) return;
        if (_vpn.Node is { } node)
        {
            node.VpnStateChanged += _onState;
            node.NetworkStatusChanged += _onNetwork;
            _watching = node;
        }
        Refresh();
    }

    private void Refresh()
    {
        if (_vpn == null) return;
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

    private async void Import_Click(object? sender, RoutedEventArgs e)
    {
        if (_vpn == null || Owner is not { } owner) return;
        var path = await Dialogs.PickOpenAsync(owner, "Import WireGuard configuration", Dialogs.WireGuard, Dialogs.AllFiles);
        if (path == null) return;
        try { _vpn.Import(path); }
        catch (Exception ex) when (ex is FormatException or System.IO.IOException or UnauthorizedAccessException)
        {
            await Dialogs.MessageAsync(owner, "Import WireGuard configuration", ex.Message);
        }
        Refresh();
    }

    private async void Toggle_Click(object? sender, RoutedEventArgs e)
    {
        if (_vpn == null || Owner is not { } owner) return;
        var vpn = _vpn;
        await RunAsync(async () =>
        {
            if ((vpn.Node?.VpnState ?? VpnState.Disconnected) == VpnState.Disconnected)
            {
                StatusText.Text = "Connecting…";
                if (!await vpn.ConnectAsync())
                    await Dialogs.MessageAsync(owner, "VPN",
                        "The VPN server did not answer. Yaparr will keep trying; check the server address and keys in the configuration.");
            }
            else await vpn.DisconnectAsync();
        });
    }

    private async void Remove_Click(object? sender, RoutedEventArgs e)
    {
        if (_vpn == null || Owner is not { } owner) return;
        if (!await Dialogs.ConfirmAsync(owner, "VPN", "Remove the saved VPN configuration?")) return;
        await RunAsync(_vpn.RemoveAsync);
    }

    private async Task RunAsync(Func<Task> action)
    {
        _busy = true;
        Refresh();
        try { await action(); }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException or FormatException)
        {
            if (Owner is { } owner) await Dialogs.MessageAsync(owner, "VPN", ex.Message);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }
}
