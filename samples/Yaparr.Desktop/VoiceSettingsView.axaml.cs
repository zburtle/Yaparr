using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yaparr.Voice.WebRtc;

namespace Yaparr.Desktop;

/// <summary>
/// The voice settings, shown in the settings overlay. A change is saved and applied as it is made; <c>changed</c> says whether the microphone
/// or speakers changed (which means opening them again) or only something that can be applied while talking.
/// </summary>
public partial class VoiceSettingsView : UserControl
{
    private const string SystemDefault = "System default";
    private AppSettings _settings = new();
    private Action<bool>? _changed;
    private Func<float?>? _level;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private bool _loading;
    private string? _pushToTalkKey;
    private bool _choosingKey;

    public VoiceSettingsView()
    {
        InitializeComponent();
        NoiseBox.ItemsSource = Enum.GetNames<NoiseSuppression>();
        if (!WebRtcVoiceProcessor.IsAvailable(out var unavailable))
        {
            EchoBox.IsEnabled = NoiseBox.IsEnabled = false;
            ProcessingNote.Text = "Not available on this system, so your microphone is sent as it is: " + unavailable;
        }

        InputBox.SelectionChanged += (_, _) => Commit(devices: true);
        OutputBox.SelectionChanged += (_, _) => Commit(devices: true);
        OutputVolumeBox.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) Commit(); };
        SensitivityBox.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) Commit(); };
        NoiseBox.SelectionChanged += (_, _) => Commit();
        EchoBox.IsCheckedChanged += (_, _) => Commit();
        LevelSpeakersBox.IsCheckedChanged += (_, _) => Commit();
        AutomaticVolumeBox.IsCheckedChanged += (_, _) => Commit();
        AlwaysBox.IsCheckedChanged += (_, _) => Commit();
        PushToTalkBox.IsCheckedChanged += (_, _) => PushToTalkToggled();
        PushToTalkSoundBox.IsCheckedChanged += (_, _) => Commit();
        KeyDown += OnKeyDown;
        AddHandler(PointerPressedEvent, OnPointerDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _timer.Tick += (_, _) => ShowLevel();
    }

    /// <summary>The level bar only moves while this section is on screen.</summary>
    internal void SetActive(bool active)
    {
        if (active) { Load(); ShowLevel(); _timer.Start(); }
        else _timer.Stop();
    }

    /// <param name="settings">The settings to show and change.</param>
    /// <param name="changed">Called after a change is saved into the settings; true when the microphone or speakers changed.</param>
    /// <param name="level">The microphone level (0 to 1) while in a voice channel, or null when not in one.</param>
    internal void Attach(AppSettings settings, Action<bool> changed, Func<float?> level)
    {
        _settings = settings;
        _changed = changed;
        _level = level;
        Load();
    }

    // Reads the settings into the controls, and the devices again (they come and go).
    internal void Load()
    {
        _loading = true;
        try
        {
            Fill(InputBox, VoiceAudio.InputNames(), _settings.VoiceInput);
            Fill(OutputBox, VoiceAudio.OutputNames(), _settings.VoiceOutput);
            OutputVolumeBox.Value = _settings.VoiceOutputVolume;
            AlwaysBox.IsChecked = _settings.VoiceAlwaysOn;
            PushToTalkBox.IsChecked = _settings.VoicePushToTalk;
            _pushToTalkKey = _settings.VoicePushToTalkKey;
            PushToTalkSoundBox.IsChecked = _settings.VoicePushToTalkSound;
            NoiseBox.SelectedItem = Enum.TryParse<NoiseSuppression>(_settings.VoiceNoiseSuppression, out var noise) ? noise.ToString() : nameof(NoiseSuppression.Moderate);
            EchoBox.IsChecked = _settings.VoiceEchoCancellation;
            LevelSpeakersBox.IsChecked = _settings.VoiceLevelSpeakers;
            AutomaticVolumeBox.IsChecked = _settings.VoiceAutomaticVolume;
            SensitivityBox.Value = _settings.VoiceSensitivity;
            _choosingKey = false;
            ShowKey();
        }
        finally { _loading = false; }
    }

    private void ShowLevel()
    {
        var level = _level?.Invoke();
        LevelBar.Value = level == null ? 0 : Math.Min(100, level.Value * 1000);
        HintText.Text = level == null ? "The bar moves while you are in a voice channel." : "";
    }

    private void Commit(bool devices = false)
    {
        if (_loading) return;
        _settings.VoiceInput = Chosen(InputBox);
        _settings.VoiceOutput = Chosen(OutputBox);
        _settings.VoiceOutputVolume = (int)OutputVolumeBox.Value;
        _settings.VoiceAlwaysOn = AlwaysBox.IsChecked == true;
        _settings.VoicePushToTalkSound = PushToTalkSoundBox.IsChecked == true;
        _settings.VoiceEchoCancellation = EchoBox.IsChecked == true;
        _settings.VoiceLevelSpeakers = LevelSpeakersBox.IsChecked == true;
        _settings.VoiceAutomaticVolume = AutomaticVolumeBox.IsChecked == true;
        _settings.VoiceNoiseSuppression = NoiseBox.SelectedItem as string ?? nameof(NoiseSuppression.Moderate);
        _settings.VoiceSensitivity = (int)SensitivityBox.Value;
        _changed?.Invoke(devices);
    }

    // Push to talk needs a key: switching it on asks for one, and it only takes effect once there is one.
    private void PushToTalkToggled()
    {
        if (_loading) return;
        if (PushToTalkBox.IsChecked == true && string.IsNullOrEmpty(_pushToTalkKey)) { StartChoosingKey(); return; }
        _settings.VoicePushToTalk = PushToTalkBox.IsChecked == true;
        Commit();
    }

    private void ShowKey() => PushToTalkKeyButton.Content = _choosingKey ? "Press a key or a mouse button… (Esc to cancel)"
        : PushToTalkTrigger.Parse(_pushToTalkKey) is not { } trigger ? "Choose a key or button…" : $"Hold: {trigger.Description} (click to change)";

    private void PushToTalkKey_Click(object? sender, RoutedEventArgs e) => StartChoosingKey();

    private void StartChoosingKey()
    {
        _choosingKey = true;
        ShowKey();
        Focus();
    }

    // The middle, back or forward button of the mouse can be chosen too (not left and right, which the click that opened this needs).
    private void OnPointerDown(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!_choosingKey || PushToTalkTrigger.ButtonOf(e.GetCurrentPoint(this).Properties) is not (> 0 and var button)) return;
        e.Handled = true;
        _choosingKey = false;
        _pushToTalkKey = "Mouse" + button;
        _settings.VoicePushToTalkKey = _pushToTalkKey;
        _loading = true;
        PushToTalkBox.IsChecked = true;
        _loading = false;
        _settings.VoicePushToTalk = true;
        Commit();
        ShowKey();
    }

    // The next key pressed becomes the push-to-talk key.
    private void OnKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (!_choosingKey) return;
        e.Handled = true;
        _choosingKey = false;
        if (e.Key != Avalonia.Input.Key.Escape)
        {
            _pushToTalkKey = e.Key.ToString();
            _settings.VoicePushToTalkKey = _pushToTalkKey;
            _loading = true;
            PushToTalkBox.IsChecked = true;
            _loading = false;
            _settings.VoicePushToTalk = true;
            Commit();
        }
        else if (string.IsNullOrEmpty(_pushToTalkKey))
        {
            _loading = true;
            PushToTalkBox.IsChecked = false;
            _loading = false;
        }
        ShowKey();
    }

    private static void Fill(ComboBox box, IReadOnlyList<string> names, string? selected)
    {
        var items = new List<string> { SystemDefault };
        items.AddRange(names);
        box.ItemsSource = items;
        box.SelectedItem = selected != null && names.Contains(selected) ? selected : SystemDefault;
    }

    private static string? Chosen(ComboBox box) => box.SelectedItem is string s && s != SystemDefault ? s : null;
}
