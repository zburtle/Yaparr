using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yaparr.Chat;

namespace Yaparr.Desktop;

/// <summary>The devices that are signed in as you right now: this one, and your others that are connected to it.</summary>
public partial class DevicesDialog : Window
{
    private readonly ChatNode? _node;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    public DevicesDialog() => InitializeComponent();

    internal DevicesDialog(ChatNode node) : this()
    {
        _node = node;
        Note.Text = "Your other devices show here while they are on and connected to this one. A message you sent from another device says so next to its time.";
        Refresh();
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        if (_node == null) return;
        DeviceList.ItemsSource = _node.MyDevices.Select(d => d.IsThisDevice
            ? $"This device  ·  {d.Id.Short}"
            : $"{d.Id.Short}  ·  {Describe(d.Link)}").ToList();
    }

    private static string Describe(PeerLink link) => link switch
    {
        PeerLink.Direct => "connected directly",
        PeerLink.Relayed => "connected through another member",
        PeerLink.ViaServer => "connected through the relay server",
        PeerLink.Reconnecting => "reconnecting",
        PeerLink.NotResponding => "connected, not responding",
        _ => "not connected",
    };

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
