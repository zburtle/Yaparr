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

public partial class NetworkCheckDialog : Window
{
    private readonly ChatNode _node = null!;
    private NetworkCheckReport? _report;

    public NetworkCheckDialog() => InitializeComponent();

    public NetworkCheckDialog(ChatNode node) : this() => _node = node;

    public async Task RunAsync()
    {
        AgainButton.IsEnabled = CopyButton.IsEnabled = false;
        SummaryText.Text = "Checking…";
        Items.ItemsSource = null;
        try
        {
            _report = await _node.CheckNetworkAsync();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException or OperationCanceledException)
        {
            SummaryText.Text = "The check could not finish: " + ex.Message;
            AgainButton.IsEnabled = true;
            return;
        }

        SummaryText.Text = _report.Summary;
        Items.ItemsSource = _report.Items.Select(i => new NetworkCheckRow(i)).ToList();
        AgainButton.IsEnabled = CopyButton.IsEnabled = true;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private async void Again_Click(object? sender, RoutedEventArgs e) => await RunAsync();

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if (_report == null) return;
        var lines = _report.Items.Select(i => $"[{i.Level}] {i.Title}: {i.Detail}");
        await Dialogs.CopyAsync(this, _report.Summary + Environment.NewLine + string.Join(Environment.NewLine, lines));
    }
}
