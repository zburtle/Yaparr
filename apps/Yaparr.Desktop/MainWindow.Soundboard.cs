using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaparr.Identity;
using Yaparr.Voice;

namespace Yaparr.Desktop;

// The room's soundboard: tiles over the window like the settings (Escape, the cross or a click outside puts it away), which can be
// popped out into a window of its own to keep beside the call, and a button beside each person in the channel that silences
// their sounds without muting their voice.
public partial class MainWindow
{
    private SoundboardView? _soundboard;
    private SoundboardWindow? _soundboardWindow;
    private IInputElement? _soundboardOpener;
    private string _soundboardRoom = "";

    private void Soundboard_Click(object? sender, RoutedEventArgs e)
    {
        if (_soundboardWindow != null) { _soundboardWindow.Activate(); return; }
        if (_soundboard != null) { CloseSoundboard(); return; }
        if (_voiceConv == null) return;
        var room = _roomConversations.FirstOrDefault(kv => kv.Value == _voiceConv).Key;
        if (room == null || _node == null) return;

        _soundboardRoom = room.Name;
        var view = new SoundboardView();
        view.Attach(room, _node.Id, _settings, () => _data.SaveSettings(_settings), () => _voice, () => _settings.VoiceOutput,
            IsPersonSoundboardMuted, SetPersonSoundboardMute, ToggleSoundboardPlace, CloseSoundboard);
        _soundboard = view;
        if (_settings.SoundboardInWindow) PopOutSoundboard(); else DockSoundboard();
    }

    // The soundboard goes over the main window.
    private void DockSoundboard()
    {
        if (_soundboard is not { } view) return;
        if (_soundboardWindow is { } window)
        {
            _soundboardWindow = null;
            window.HandedBack = true;
            window.Content = null;
            window.Close();
        }
        else _soundboardOpener = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        view.Docked = true;
        SoundboardHost.Child = view;
        SoundboardOverlay.IsVisible = true;
        // Focus goes into the sounds, so the keyboard works at once.
        Avalonia.Threading.Dispatcher.UIThread.Post(view.FocusFirst);
    }

    // The soundboard goes to a window of its own.
    private void PopOutSoundboard()
    {
        if (_soundboard is not { } view || _soundboardWindow != null) return;
        SoundboardOverlay.IsVisible = false;
        SoundboardHost.Child = null;
        view.Docked = false;
        var window = new SoundboardWindow(view, _soundboardRoom);
        _soundboardWindow = window;
        window.Closed += (_, _) =>
        {
            if (window.HandedBack) return;
            if (_soundboardWindow == window) _soundboardWindow = null;
            EndSoundboard();
        };
        window.Show(this);
    }

    private void ToggleSoundboardPlace()
    {
        bool toWindow = _soundboardWindow == null;
        _settings.SoundboardInWindow = toWindow;
        _data.SaveSettings(_settings);
        if (toWindow) PopOutSoundboard(); else DockSoundboard();
    }

    // Puts the soundboard away, wherever it is.
    private void CloseSoundboard()
    {
        if (_soundboardWindow is { } window)
        {
            // Closing the window ends the board (see PopOutSoundboard).
            window.Close();
            return;
        }
        EndSoundboard();
    }

    private void EndSoundboard()
    {
        var view = _soundboard;
        _soundboard = null;
        view?.Detach();
        SoundboardOverlay.IsVisible = false;
        SoundboardHost.Child = null;
        // Focus goes back to where it was, so the keyboard carries on from the same place.
        (_soundboardOpener as Control)?.Focus();
        _soundboardOpener = null;
    }

    // A click on the dark area around the panel puts the soundboard away; a click on the panel itself does not reach it.
    private void SoundboardOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == SoundboardOverlay) CloseSoundboard();
    }

    private void SoundboardOverlay_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseSoundboard();
        e.Handled = true;
    }

    // Applies what was chosen last time: everybody muted or not, how loud, and the people who are silenced.
    private void ApplySoundboardSettings(VoiceSession session)
    {
        session.SoundboardsMuted = _settings.SoundboardsMuted;
        session.SoundboardVolume = _settings.SoundboardVolume / 100f;
        foreach (var id in _settings.SoundboardMuted) session.SetSoundboardMute(new PeerId(id), true);
    }

    private bool IsPersonSoundboardMuted(string id) => _settings.SoundboardMuted.Contains(id);

    private void SetPersonSoundboardMute(string id, bool muted)
    {
        if (muted) { if (!_settings.SoundboardMuted.Contains(id)) _settings.SoundboardMuted.Add(id); }
        else _settings.SoundboardMuted.Remove(id);
        _data.SaveSettings(_settings);
        _voice?.SetSoundboardMute(new PeerId(id), muted);
        foreach (var conv in _conversations)
            foreach (var member in conv.VoiceMembers.Where(m => m.Id == id)) member.SoundboardMuted = muted;
    }

    private void SoundboardMute_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Avalonia.Controls.Control { DataContext: VoiceMemberVm member } && !member.IsSelf)
            SetPersonSoundboardMute(member.Id, !member.SoundboardMuted);
    }
}
