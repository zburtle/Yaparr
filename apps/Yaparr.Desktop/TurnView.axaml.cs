using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yaparr.Chat;
using Yaparr.Transport;

namespace Yaparr.Desktop;

/// <summary>The TURN relay, shown in the settings under Network. Saving needs a press of Save, since it connects to the server.</summary>
public partial class TurnView : UserControl
{
    private Func<ChatNode?> _node = () => null;
    private string _path = "";
    private ChatNode? _watching;
    private readonly Action<NetworkStatus> _onNetwork;

    public TurnView()
    {
        InitializeComponent();
        _onNetwork = _ => Dispatcher.UIThread.Post(ShowStatus);
    }

    internal void Attach(Func<ChatNode?> node, string settingsPath)
    {
        _node = node;
        _path = settingsPath;
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    /// <summary>Called when the section is shown or put away: reads the saved relay and follows the relay's state only while on screen.</summary>
    internal void SetActive(bool active)
    {
        if (_watching != null) { _watching.NetworkStatusChanged -= _onNetwork; _watching = null; }
        if (!active) return;

        var turn = TraversalOptions.Load(_path).Turn;
        EnabledBox.IsChecked = turn?.Enabled ?? false;
        ServerBox.Text = turn?.Server ?? "";
        UserBox.Text = turn?.Username ?? "";
        PasswordBox.Text = turn?.Credential ?? "";
        AlwaysBox.IsChecked = turn?.Always ?? false;
        if (_node() is { } node) { node.NetworkStatusChanged += _onNetwork; _watching = node; }
        ShowStatus();
    }

    private void ShowStatus() => StatusText.Text = _node()?.NetworkStatus.Relay is { } relay ? $"Relay active at {relay}." : "";

    private async void Share_Click(object? sender, RoutedEventArgs e)
    {
        if (Owner is not { } owner) return;
        var server = ServerBox.Text?.Trim() ?? "";
        if (server.Length == 0)
        {
            await Dialogs.MessageAsync(owner, "Share this relay", "Enter the TURN server address first.");
            return;
        }
        var code = RelayShare.Create(new TurnOptions { Server = server, Username = UserBox.Text?.Trim() ?? "", Credential = PasswordBox.Text ?? "" });
        var choice = await Dialogs.ChoiceAsync(owner, "Share this relay",
            "The code holds this relay's address, user name and password. Anyone who has it can use your relay, and the bandwidth is yours, so send it only to people you trust, by a private route.",
            "Copy the code", "Save to a file…", "Cancel");
        if (choice == "Copy the code")
            StatusText.Text = await Dialogs.CopyAsync(owner, code) ? "Copied. Paste it into a message to the person you are sharing with." : "Could not reach the clipboard.";
        else if (choice == "Save to a file…")
        {
            var path = await Dialogs.PickSaveAsync(owner, "Save the relay file", "relay.yrelay", Dialogs.RelayFile);
            if (path == null) return;
            try
            {
                System.IO.File.WriteAllText(path, code + Environment.NewLine);
                StatusText.Text = "Saved. Send the file to the person you are sharing with.";
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                await Dialogs.MessageAsync(owner, "Share this relay", ex.Message);
            }
        }
    }

    private async void Import_Click(object? sender, RoutedEventArgs e)
    {
        if (Owner is not { } owner) return;
        var choice = await Dialogs.ChoiceAsync(owner, "Import a relay",
            "Paste the relay code that was sent to you, or open the relay file. The relay is filled in here for you to look at; nothing changes until you press Save.",
            "Paste from the clipboard", "Open a file…", "Cancel");
        string? text = null;
        if (choice == "Paste from the clipboard") text = await Dialogs.ClipboardTextAsync(owner);
        else if (choice == "Open a file…")
        {
            var path = await Dialogs.PickOpenAsync(owner, "Open a relay file", Dialogs.RelayFile, Dialogs.AllFiles);
            if (path == null) return;
            try
            {
                if (new System.IO.FileInfo(path).Length > 4096) { await Dialogs.MessageAsync(owner, "Import a relay", "That is not a Yaparr relay file."); return; }
                text = System.IO.File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                await Dialogs.MessageAsync(owner, "Import a relay", ex.Message);
                return;
            }
        }
        else return;

        if (!RelayShare.TryParse(text, out var turn) || turn == null)
        {
            await Dialogs.MessageAsync(owner, "Import a relay", choice == "Paste from the clipboard"
                ? "The clipboard does not hold a Yaparr relay code. Copy the whole code, which starts with yaparr-relay:, and try again."
                : "That is not a Yaparr relay file.");
            return;
        }
        if ((ServerBox.Text?.Trim() ?? "").Length > 0 && !string.Equals(ServerBox.Text!.Trim(), turn.Server, StringComparison.OrdinalIgnoreCase)
            && !await Dialogs.ConfirmAsync(owner, "Import a relay", $"Replace {ServerBox.Text!.Trim()} with {turn.Server}?", "Replace", "Keep mine"))
            return;
        ServerBox.Text = turn.Server;
        UserBox.Text = turn.Username;
        PasswordBox.Text = turn.Credential;
        EnabledBox.IsChecked = true;
        StatusText.Text = $"Imported {turn.Server}. Press Save to use it.";
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (Owner is not { } owner) return;
        var server = ServerBox.Text?.Trim() ?? "";
        bool enabled = EnabledBox.IsChecked == true;
        if (enabled && server.Length == 0)
        {
            await Dialogs.MessageAsync(owner, "TURN relay", "Enter the TURN server address, for example turn.example.com:3478.");
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
            await Dialogs.MessageAsync(owner, "TURN relay", ex.Message);
            return;
        }

        SaveButton.IsEnabled = false;
        StatusText.Text = enabled ? "Connecting to the relay…" : "Saved.";
        try
        {
            if (_node() is { } node) await node.ConfigureTurnAsync(turn);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
        if (enabled && _node()?.NetworkStatus.Relay == null)
            StatusText.Text = "Saved, but the relay did not allocate. Check the server, username and password. The relay is only requested when needed unless \"Always relay\" is on.";
        else ShowStatus();
    }
}
