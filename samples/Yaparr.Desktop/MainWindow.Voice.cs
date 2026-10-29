using Avalonia.Interactivity;
using Yaparr.Chat;
using Yaparr.Voice;
using Yaparr.Voice.WebRtc;

namespace Yaparr.Desktop;

// Voice channels: join from a room's header, with the people already in the channel listed under the buttons.
public partial class MainWindow
{
    private VoiceSession? _voice;
    private VoiceAudio? _voiceAudio;
    private ConversationVm? _voiceConv;
    private WebRtcVoiceProcessor? _processor;
    private bool _processingWarned;

    // With announce set, people arriving in or leaving the channel you are in are heard.
    private void RefreshVoice(ConversationVm conv, ChatRoom room, bool announce = true)
    {
        var existing = conv.VoiceMembers.ToDictionary(m => m.Id);
        conv.VoiceMembers.Clear();
        bool someoneJoined = false;
        foreach (var p in room.VoiceParticipants)
        {
            if (!existing.TryGetValue(p.Peer.Id.Value, out var old) && !p.IsSelf) someoneJoined = true;
            var vm = old ?? new VoiceMemberVm(p.Peer.Id.Value, p.Peer.DisplayName, p.IsSelf);
            vm.Name = p.Peer.DisplayName;
            vm.Muted = p.Muted;
            vm.Deafened = p.Deafened;
            conv.VoiceMembers.Add(vm);
        }
        conv.VoiceMembersChanged();

        if (!announce || !conv.InVoice || _voiceConv != conv) return;
        var now = conv.VoiceMembers.Select(m => m.Id).ToHashSet();
        bool someoneLeft = existing.Values.Any(m => !m.IsSelf && !now.Contains(m.Id));
        if (someoneJoined) _sounds.SomeoneJoined();
        else if (someoneLeft) _sounds.SomeoneLeft();
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

    private VoiceProcessing ProcessingSettings => new(
        _settings.VoiceEchoCancellation,
        Enum.TryParse<NoiseSuppression>(_settings.VoiceNoiseSuppression, out var noise) ? noise : NoiseSuppression.Moderate);

    // Echo cancellation and noise suppression on the microphone. If the library cannot load here, voice works without it.
    private void ApplyProcessing(VoiceSession session)
    {
        var wanted = ProcessingSettings;
        if (wanted.IsOff)
        {
            session.Processor = null;
            _processor?.Dispose();
            _processor = null;
            return;
        }
        if (_processor != null)
        {
            _processor.Reconfigure(wanted);
            session.Processor = _processor;
            return;
        }
        if (WebRtcVoiceProcessor.TryCreate(wanted, out var processor, out var error))
        {
            _processor = processor;
            session.Processor = processor;
        }
        else if (!_processingWarned)
        {
            _processingWarned = true;
            _log.Write("voice processing is not available: " + error);
            StatusText.Text = "Echo cancellation and noise suppression are not available on this system, so your microphone is sent as it is.";
        }
    }

    private void JoinVoice_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null || _selected is not { IsRoom: true } conv) return;
        var room = _roomConversations.FirstOrDefault(kv => kv.Value == conv).Key;
        if (room == null) return;

        LeaveVoiceCore();
        var session = room.JoinVoice();
        ApplyVoiceSettings(session);
        ApplyProcessing(session);
        session.ProcessorFailed += ex => UI(() =>
        {
            _log.Write("voice processing stopped: " + ex.Message);
            _processor?.Dispose();
            _processor = null;
            StatusText.Text = "Echo cancellation and noise suppression stopped working, so your microphone is sent as it is.";
        });
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
        RefreshVoice(conv, room, announce: false);
        _sounds.YouJoined();
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
        _processor?.Dispose();
        _processor = null;
        if (conv != null)
        {
            conv.InVoice = false;
            conv.Muted = conv.Deafened = false;
            var room = _roomConversations.FirstOrDefault(kv => kv.Value == conv).Key;
            if (room != null) RefreshVoice(conv, room, announce: false);
            _sounds.YouLeft();
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
        ApplyProcessing(_voice);
        _voiceAudio?.Dispose();
        _voiceAudio = null;
        try { _voiceAudio = new VoiceAudio(_voice, _settings.VoiceInput, _settings.VoiceOutput); }
        catch (Exception ex) { StatusText.Text = $"Couldn't open your microphone or speakers: {ex.Message}"; LeaveVoiceCore(); }
    }
}
