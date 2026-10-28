using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yaparr.Chat;
using Yaparr.Transport;

namespace Yaparr.Desktop;

public partial class TurnDialog : Window
{
    private readonly ChatNode? _node;
    private readonly string _path = "";

    public TurnDialog() => InitializeComponent();

    public TurnDialog(ChatNode? node, string settingsPath) : this()
    {
        _node = node;
        _path = settingsPath;
        var saved = TraversalOptions.Load(settingsPath);
        var turn = saved.Turn;
        DefaultBox.IsChecked = saved.UseDefaultRelay;
        DefaultBox.IsVisible = BuiltInRelay.Load() != null;
        if (turn != null)
        {
            EnabledBox.IsChecked = turn.Enabled;
            ServerBox.Text = turn.Server;
            UserBox.Text = turn.Username;
            PasswordBox.Text = turn.Credential;
            AlwaysBox.IsChecked = turn.Always;
        }
        ShowStatus();
        if (node != null)
        {
            Action<NetworkStatus> changed = _ => Dispatcher.UIThread.Post(ShowStatus);
            node.NetworkStatusChanged += changed;
            Closed += (_, _) => node.NetworkStatusChanged -= changed;
        }
    }

    private void ShowStatus()
    {
        StatusText.Text = _node?.NetworkStatus.Relay is { } relay ? $"Relay active at {relay}." : "";
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        var server = ServerBox.Text?.Trim() ?? "";
        bool enabled = EnabledBox.IsChecked == true;
        bool useDefault = DefaultBox.IsChecked == true;
        if (enabled && server.Length == 0)
        {
            await Dialogs.MessageAsync(this, "TURN relay", "Enter the TURN server address, for example turn.example.com:3478.");
            return;
        }

        TurnOptions? turn = server.Length == 0 && !enabled
            ? null
            : new TurnOptions
            {
                Server = server,
                Username = UserBox.Text?.Trim() ?? "",
                Credential = PasswordBox.Text ?? "",
                Enabled = enabled,
                Always = AlwaysBox.IsChecked == true,
            };
        try
        {
            TraversalOptions.SaveTurn(_path, turn);
            TraversalOptions.SaveDefaultRelay(_path, useDefault);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            await Dialogs.MessageAsync(this, "TURN relay", ex.Message);
            return;
        }

        SaveButton.IsEnabled = false;
        StatusText.Text = enabled ? "Connecting to the relay…" : "Saved.";
        try
        {
            if (_node != null) await _node.ConfigureTurnAsync(turn, useDefault);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
        if (enabled && _node?.NetworkStatus.Relay == null)
            StatusText.Text = "Saved, but the relay did not allocate. Check the server, username and password. The relay is only requested when needed unless \"Always relay\" is on.";
        else ShowStatus();
    }
}
