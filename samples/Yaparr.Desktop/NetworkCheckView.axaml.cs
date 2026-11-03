using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Yaparr.Chat;

namespace Yaparr.Desktop;

public sealed class NetworkCheckRow
{
    private readonly CheckItem _item;
    public NetworkCheckRow(CheckItem item) => _item = item;
    public string Title => _item.Title;
    public string Detail => _item.Detail;
    public string Glyph => _item.Level switch { CheckLevel.Ok => "✓", CheckLevel.Info => "•", CheckLevel.Warning => "!", _ => "✕" };
    public IBrush Brush => _item.Level switch
    {
        CheckLevel.Ok => Brushes.SeaGreen,
        CheckLevel.Info => Brushes.Gray,
        CheckLevel.Warning => Brushes.DarkOrange,
        _ => Brushes.Firebrick,
    };
}

/// <summary>The connection check, shown in the settings under Network. It runs when the section is opened and again on request.</summary>
public partial class NetworkCheckView : UserControl
{
    private Func<ChatNode?> _node = () => null;
    private NetworkCheckReport? _report;
    private bool _running;

    public NetworkCheckView() => InitializeComponent();

    internal void Attach(Func<ChatNode?> node) => _node = node;

    /// <summary>Called when the section is shown.</summary>
    internal void Activate() => _ = RunAsync();

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    public async Task RunAsync()
    {
        if (_running || _node() is not { } node) return;
        _running = true;
        AgainButton.IsEnabled = CopyButton.IsEnabled = false;
        SummaryText.Text = "Checking…";
        Items.ItemsSource = null;
        try
        {
            _report = await node.CheckNetworkAsync();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException or OperationCanceledException)
        {
            SummaryText.Text = "The check could not finish: " + ex.Message;
            AgainButton.IsEnabled = true;
            _running = false;
            return;
        }

        SummaryText.Text = _report.Summary;
        Items.ItemsSource = _report.Items.Select(i => new NetworkCheckRow(i)).ToList();
        AgainButton.IsEnabled = CopyButton.IsEnabled = true;
        _running = false;
    }

    private async void Again_Click(object? sender, RoutedEventArgs e) => await RunAsync();

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if (_report == null || Owner is not { } owner) return;
        var lines = _report.Items.Select(i => $"[{i.Level}] {i.Title}: {i.Detail}");
        await Dialogs.CopyAsync(owner, _report.Summary + Environment.NewLine + string.Join(Environment.NewLine, lines));
    }
}
