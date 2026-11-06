using Avalonia.Interactivity;
using Yaparr.Identity;
using Yaparr.Voice;

namespace Yaparr.Desktop;

// The room's soundboard: a window of tiles opened from the voice controls, and a button beside each person in the channel
// that silences their sounds without muting their voice.
public partial class MainWindow
{
    private SoundboardWindow? _soundboard;

    private void Soundboard_Click(object? sender, RoutedEventArgs e)
    {
        if (_soundboard != null) { _soundboard.Activate(); return; }
        if (_voiceConv == null) return;
        var room = _roomConversations.FirstOrDefault(kv => kv.Value == _voiceConv).Key;
        if (room == null || _node == null) return;

        var window = new SoundboardWindow(room, _node.Id, _settings, () => _data.SaveSettings(_settings), () => _voice, () => _settings.VoiceOutput,
            IsPersonSoundboardMuted, SetPersonSoundboardMute);
        window.Closed += (_, _) => { if (_soundboard == window) _soundboard = null; };
        _soundboard = window;
        window.Show(this);
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
