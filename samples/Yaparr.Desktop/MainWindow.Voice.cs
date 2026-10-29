using Avalonia.Interactivity;
using Yaparr.Chat;
using Yaparr.Voice;

namespace Yaparr.Desktop;

// Voice channels: join from a room's header, with the people already in the channel listed under the buttons.
public partial class MainWindow
{
    private VoiceSession? _voice;
    private VoiceAudio? _voiceAudio;
    private ConversationVm? _voiceConv;

    private void RefreshVoice(ConversationVm conv, ChatRoom room)
    {
        var existing = conv.VoiceMembers.ToDictionary(m => m.Id);
        conv.VoiceMembers.Clear();
        foreach (var p in room.VoiceParticipants)
        {
            var vm = existing.TryGetValue(p.Peer.Id.Value, out var old) ? old : new VoiceMemberVm(p.Peer.Id.Value, p.Peer.DisplayName, p.IsSelf);
            vm.Muted = p.Muted;
            vm.Deafened = p.Deafened;
            conv.VoiceMembers.Add(vm);
        }
        conv.VoiceMembersChanged();
    }

    private void OnSpeaking(ConversationVm conv, Yaparr.Identity.PeerId id, bool speaking)
    {
        var member = conv.VoiceMembers.FirstOrDefault(m => m.Id == id.Value);
        if (member != null) member.Speaking = speaking;
    }

    private void ApplyVoiceSettings(VoiceSession session)
    {
        session.Activation = _settings.VoiceAlwaysOn ? VoiceActivation.Open : VoiceActivation.VoiceActivity;
        session.Sensitivity = _settings.VoiceSensitivity / 1000f;
    }

    private void JoinVoice_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null || _selected is not { IsRoom: true } conv) return;
        var room = _roomConversations.FirstOrDefault(kv => kv.Value == conv).Key;
        if (room == null) return;

        LeaveVoiceCore();
        var session = room.JoinVoice();
        ApplyVoiceSettings(session);
        try
        {
            _voiceAudio = new VoiceAudio(session, _settings.VoiceInput, _settings.VoiceOutput);
        }
        catch (Exception ex)
        {
            // The audio layer reports a missing or busy device in several ways; all of them mean we cannot join.
            session.Leave();
            StatusText.Text = $"Couldn't open your microphone or speakers: {ex.Message}";
            return;
        }
        session.SpeakingChanged += (id, speaking) => UI(() => OnSpeaking(conv, id, speaking));
        _voice = session;
        _voiceConv = conv;
        conv.InVoice = true;
        conv.Muted = conv.Deafened = false;
        RefreshVoice(conv, room);
    }

    private void LeaveVoice_Click(object? sender, RoutedEventArgs e) => LeaveVoiceCore();

    private void LeaveVoiceCore()
    {
        var session = _voice;
        var conv = _voiceConv;
        _voice = null;
        _voiceConv = null;
        _voiceAudio?.Dispose();
        _voiceAudio = null;
        session?.Leave();
        if (conv != null)
        {
            conv.InVoice = false;
            conv.Muted = conv.Deafened = false;
            var room = _roomConversations.FirstOrDefault(kv => kv.Value == conv).Key;
            if (room != null) RefreshVoice(conv, room);
        }
    }

    private void MuteVoice_Click(object? sender, RoutedEventArgs e)
    {
        if (_voice == null || _voiceConv == null) return;
        _voice.Muted = !_voice.Muted;
        _voiceConv.Muted = _voice.Muted;
    }

    private void DeafenVoice_Click(object? sender, RoutedEventArgs e)
    {
        if (_voice == null || _voiceConv == null) return;
        _voice.Deafened = !_voice.Deafened;
        _voiceConv.Deafened = _voice.Deafened;
    }

    private async void VoiceSettings_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new VoiceSettingsDialog(_settings, _voice == null ? null : () => _voice.InputLevel);
        await dialog.ShowDialog(this);
        if (!dialog.Saved) return;
        _data.SaveSettings(_settings);
        if (_voice == null) return;

        ApplyVoiceSettings(_voice);
        _voiceAudio?.Dispose();
        _voiceAudio = null;
        try { _voiceAudio = new VoiceAudio(_voice, _settings.VoiceInput, _settings.VoiceOutput); }
        catch (Exception ex) { StatusText.Text = $"Couldn't open your microphone or speakers: {ex.Message}"; LeaveVoiceCore(); }
    }
}
