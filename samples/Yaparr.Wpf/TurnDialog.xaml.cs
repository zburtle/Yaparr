using System.Windows;
using Yaparr.Chat;
using Yaparr.Transport;

namespace Yaparr.Wpf;

public partial class TurnDialog : Window
{
    private readonly ChatNode? _node;
    private readonly string _path;

    public TurnDialog(ChatNode? node, string settingsPath)
    {
        _node = node;
        _path = settingsPath;
        InitializeComponent();
        var turn = TraversalOptions.Load(settingsPath).Turn;
        if (turn != null)
        {
            EnabledBox.IsChecked = turn.Enabled;
            ServerBox.Text = turn.Server;
            UserBox.Text = turn.Username;
            PasswordBox.Password = turn.Credential;
            AlwaysBox.IsChecked = turn.Always;
        }
        ShowStatus();
        if (node != null)
        {
            Action<NetworkStatus> changed = _ => Dispatcher.BeginInvoke(ShowStatus);
            node.NetworkStatusChanged += changed;
            Closed += (_, _) => node.NetworkStatusChanged -= changed;
        }
    }

    private void ShowStatus()
    {
        StatusText.Text = _node?.NetworkStatus.Relay is { } relay ? $"Relay active at {relay}." : "";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var server = ServerBox.Text.Trim();
        bool enabled = EnabledBox.IsChecked == true;
        if (enabled && server.Length == 0)
        {
            MessageBox.Show(this, "Enter the TURN server address, for example turn.example.com:3478.", "TURN relay", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        TurnOptions? turn = server.Length == 0 && !enabled
            ? null
            : new TurnOptions { Server = server, Username = UserBox.Text.Trim(), Credential = PasswordBox.Password, Enabled = enabled, Always = AlwaysBox.IsChecked == true };
        try
        {
            TraversalOptions.SaveTurn(_path, turn);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "TURN relay", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        SaveButton.IsEnabled = false;
        StatusText.Text = enabled ? "Connecting to the relay…" : "Saved.";
        try
        {
            if (_node != null) await _node.ConfigureTurnAsync(turn);
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
