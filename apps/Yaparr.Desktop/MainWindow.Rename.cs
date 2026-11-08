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

    // The pencil next to your name shows while the pointer is over the profile header.
    private void ProfileToggle_PointerEntered(object? sender, Avalonia.Input.PointerEventArgs e) => RenameButton.IsVisible = true;

    private void ProfileToggle_PointerExited(object? sender, Avalonia.Input.PointerEventArgs e) => RenameButton.IsVisible = RenameButton.IsPointerOver;

    // A click on your ID copies it.
    private void ProfileId_PointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == Avalonia.Input.MouseButton.Left) CopyId_Click(sender, new Avalonia.Interactivity.RoutedEventArgs());
    }

    // Your name appears in the title bar and in the profile header at the top left, with its coloured initial.
    private void ShowOwnName(string name)
    {
        // Where the menu row is the title bar, the name of the app is shown in the middle of it, and the window has no title of its own to draw.
        Title = MenuRowIsTitleBar ? "" : $"Yaparr — {name}";
        NameText.Text = name;
        var picture = _node == null ? null : AvatarPictures.For(_node.AccountId.Value);
        ProfileInitial.Text = picture != null ? "" : Avatars.Initial(name);
        ProfileAvatar.Background = picture ?? Avatars.BrushFor(name);
    }

    private void OnPeerRenamed(Yaparr.Chat.PeerInfo peer)
    {
        Remember(peer);
        if (_directConversations.TryGetValue(peer.Account, out var direct)) direct.Title = peer.DisplayName + " (direct)";
        RefreshPeers();
    }
}
