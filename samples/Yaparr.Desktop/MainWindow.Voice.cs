using Avalonia.Interactivity;
using Avalonia.Threading;
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
        var voiceChannels = room.Channels.Where(c => c.Kind == ChannelKind.Voice).ToList();
        foreach (var p in room.VoiceParticipants)
        {
            if (!existing.TryGetValue(p.Peer.Id.Value, out var old) && !p.IsSelf) someoneJoined = true;
            var vm = old;
            if (vm == null)
            {
                vm = new VoiceMemberVm(p.Peer.Id.Value, p.Peer.DisplayName, p.IsSelf);
                vm.VolumeEdited += OnPersonVolumeEdited;
            }
            vm.Name = p.Peer.DisplayName;
            vm.Account = p.Peer.Account.Value;
            double saved = _settings.PersonVolumes.TryGetValue(vm.Account, out var percent) ? percent : 100;
            vm.ShowVolume(saved);
            if (!p.IsSelf && saved != 100 && _voice is { } heard && _voiceConv == conv) heard.SetVolume(p.Peer.Id, (float)(saved / 100));
            vm.Muted = p.Muted;
            vm.SoundboardMuted = IsPersonSoundboardMuted(p.Peer.Id.Value);
            vm.Deafened = p.Deafened;
            vm.Where = voiceChannels.Count > 1 && voiceChannels.FirstOrDefault(c => c.Id == p.Channel)?.Name is { } where ? "\u00B7 " + where : "";
            conv.VoiceMembers.Add(vm);
        }
        UpdateVoiceChannels(conv, room);
        conv.VoiceMembersChanged();
        if (conv == _selected) UpdateHeaderSubtitle();

        if (!announce || !conv.InVoice || _voiceConv != conv) return;
        var now = conv.VoiceMembers.Select(m => m.Id).ToHashSet();
        bool someoneLeft = existing.Values.Any(m => !m.IsSelf && !now.Contains(m.Id));
        if (someoneJoined) _sounds.SomeoneJoined();
        else if (someoneLeft) _sounds.SomeoneLeft();
    }

    private DispatcherTimer? _volumeSaveTimer;

    // The slider next to a person in the voice list: they are heard at that level now, and the level is kept for next time.
    private void OnPersonVolumeEdited(VoiceMemberVm edited)
    {
        if (edited.Account.Length == 0) return;
        if (edited.Volume == 100) _settings.PersonVolumes.Remove(edited.Account);
        else _settings.PersonVolumes[edited.Account] = (int)edited.Volume;

        // The same person on another device of theirs, or in another room, sounds the same.
        foreach (var conv in _roomConversations.Values)
            foreach (var other in conv.VoiceMembers.Where(m => m != edited && m.Account == edited.Account)) other.ShowVolume(edited.Volume);
        if (_voice is { } session && _voiceConv != null)
            foreach (var member in _voiceConv.VoiceMembers.Where(m => !m.IsSelf && m.Account == edited.Account))
                session.SetVolume(new Yaparr.Identity.PeerId(member.Id), (float)(edited.Volume / 100));

        // A slider makes many changes a second; the settings are written once it stops.
        _volumeSaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _volumeSaveTimer.Tick -= SaveVolumes;
        _volumeSaveTimer.Tick += SaveVolumes;
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();
    }

    private void SaveVolumes(object? sender, EventArgs e)
    {
        _volumeSaveTimer?.Stop();
        _data.SaveSettings(_settings);
    }

    private void ResetVolume_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Avalonia.Controls.Control { DataContext: VoiceMemberVm member }) member.Volume = 100;
    }

    // Under the room's name: who is around, and who is in voice.
    private void UpdateHeaderSubtitle()
    {
        if (_selected is not { } conv) return;
        if (!conv.IsRoom) { HeaderSubtitle.Text = conv.Subtitle; return; }
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(conv.MembersText)) parts.Add(conv.MembersText);
        if (conv.InVoice) parts.Add("you're in voice");
        else if (conv.VoiceMembers.Count > 0) parts.Add($"{conv.VoiceMembers.Count} in voice");
        HeaderSubtitle.Text = parts.Count > 0 ? string.Join(" · ", parts) : conv.Subtitle;
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
        session.OutputGain = Math.Clamp(_settings.VoiceOutputVolume, 0, 300) / 100f;
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
        JoinVoiceIn(conv, room, RoomChannels.DefaultVoice);
    }

    // Joins a voice channel of the room, leaving the one you are in (in this room or another).
    private void JoinVoiceIn(ConversationVm conv, ChatRoom room, string channelId)
    {
        if (_node == null) return;
        if (_voice != null && _voiceConv == conv && _voice.ChannelId == channelId) return;
        LeaveVoiceCore();
        VoiceSession session;
        try { session = room.JoinVoice(channelId); }
        catch (InvalidOperationException ex) { StatusText.Text = ex.Message; return; }
        ApplyVoiceSettings(session);
        ApplyProcessing(session);
        ApplySoundboardSettings(session);
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
        RefreshShares(conv, room);
        _sounds.YouJoined();
    }

    private void LeaveVoice_Click(object? sender, RoutedEventArgs e) => LeaveVoiceCore();

    private void LeaveVoiceCore()
    {
        var session = _voice;
        var conv = _voiceConv;
        _voice = null;
        _voiceConv = null;
        // Sharing a screen and watching one go with being in voice.
        if (_sharer != null && _sharerConv == conv) StopSharingCore();
        foreach (var window in _screenViews.Values.ToList()) window.Close();
        _soundboard?.Close();
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
            if (room != null) { RefreshVoice(conv, room, announce: false); RefreshShares(conv, room); }
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
