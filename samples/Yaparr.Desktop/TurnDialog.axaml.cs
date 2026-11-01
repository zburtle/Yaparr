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

    private async void Share_Click(object? sender, RoutedEventArgs e)
    {
        var server = ServerBox.Text?.Trim() ?? "";
        if (server.Length == 0)
        {
            await Dialogs.MessageAsync(this, "Share this relay", "Enter the TURN server address first.");
            return;
        }
        var code = RelayShare.Create(new TurnOptions { Server = server, Username = UserBox.Text?.Trim() ?? "", Credential = PasswordBox.Text ?? "" });
        var choice = await Dialogs.ChoiceAsync(this, "Share this relay",
            "The code holds this relay's address, user name and password. Anyone who has it can use your relay, and the bandwidth is yours, so send it only to people you trust, by a private route.",
            "Copy the code", "Save to a file…", "Cancel");
        if (choice == "Copy the code")
            StatusText.Text = await Dialogs.CopyAsync(this, code) ? "Copied. Paste it into a message to the person you are sharing with." : "Could not reach the clipboard.";
        else if (choice == "Save to a file…")
        {
            var path = await Dialogs.PickSaveAsync(this, "Save the relay file", "relay.yrelay", Dialogs.RelayFile);
            if (path == null) return;
            try
            {
                System.IO.File.WriteAllText(path, code + Environment.NewLine);
                StatusText.Text = "Saved. Send the file to the person you are sharing with.";
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                await Dialogs.MessageAsync(this, "Share this relay", ex.Message);
            }
        }
    }

    private async void Import_Click(object? sender, RoutedEventArgs e)
    {
        var choice = await Dialogs.ChoiceAsync(this, "Import a relay",
            "Paste the relay code that was sent to you, or open the relay file. The relay is filled in here for you to look at; nothing changes until you press Save.",
            "Paste from the clipboard", "Open a file…", "Cancel");
        string? text = null;
        if (choice == "Paste from the clipboard") text = await Dialogs.ClipboardTextAsync(this);
        else if (choice == "Open a file…")
        {
            var path = await Dialogs.PickOpenAsync(this, "Open a relay file", Dialogs.RelayFile, Dialogs.AllFiles);
            if (path == null) return;
            try
            {
                if (new System.IO.FileInfo(path).Length > 4096) { await Dialogs.MessageAsync(this, "Import a relay", "That is not a Yaparr relay file."); return; }
                text = System.IO.File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                await Dialogs.MessageAsync(this, "Import a relay", ex.Message);
                return;
            }
        }
        else return;

        if (!RelayShare.TryParse(text, out var turn) || turn == null)
        {
            await Dialogs.MessageAsync(this, "Import a relay", choice == "Paste from the clipboard"
                ? "The clipboard does not hold a Yaparr relay code. Copy the whole code, which starts with yaparr-relay:, and try again."
                : "That is not a Yaparr relay file.");
            return;
        }
        if ((ServerBox.Text?.Trim() ?? "").Length > 0 && !string.Equals(ServerBox.Text!.Trim(), turn.Server, StringComparison.OrdinalIgnoreCase)
            && !await Dialogs.ConfirmAsync(this, "Import a relay", $"Replace {ServerBox.Text!.Trim()} with {turn.Server}?", "Replace", "Keep mine"))
            return;
        ServerBox.Text = turn.Server;
        UserBox.Text = turn.Username;
        PasswordBox.Text = turn.Credential;
        EnabledBox.IsChecked = true;
        StatusText.Text = $"Imported {turn.Server}. Press Save to use it.";
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        var server = ServerBox.Text?.Trim() ?? "";
        bool enabled = EnabledBox.IsChecked == true;
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
