namespace Yaparr.Desktop;

// Changing the name others see, and showing other people's new names.
public partial class MainWindow
{
    private async void Rename_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_node == null) return;
        var name = await PromptDialog.AskAsync(this, "Your new name (everyone you are connected to will see it):", _node.DisplayName);
        if (string.IsNullOrWhiteSpace(name) || name == _node.DisplayName) return;
        try
        {
            _node.Rename(name);
        }
        catch (ArgumentException ex)
        {
            StatusText.Text = ex.Message;
            return;
        }
        _settings.DisplayName = _node.DisplayName;
        _data.SaveSettings(_settings);
        ShowOwnName(_node.DisplayName);
        StatusText.Text = $"You are now {_node.DisplayName}. Messages you already sent keep the old name.";
    }

    // Your name appears in the title bar and in the profile header at the top left, with its coloured initial.
    private void ShowOwnName(string name)
    {
        Title = $"Yaparr — {name}";
        NameText.Text = name;
        ProfileInitial.Text = Avatars.Initial(name);
        ProfileAvatar.Background = Avatars.BrushFor(name);
    }

    private void OnPeerRenamed(Yaparr.Chat.PeerInfo peer)
    {
        Remember(peer);
        if (_directConversations.TryGetValue(peer.Account, out var direct)) direct.Title = peer.DisplayName + " (direct)";
        RefreshPeers();
    }
}
