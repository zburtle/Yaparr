using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yaparr.Voice.WebRtc;

namespace Yaparr.Desktop;

public partial class VoiceSettingsDialog : Window
{
    private const string SystemDefault = "System default";
    private readonly AppSettings _settings = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(60) };

    public VoiceSettingsDialog() => InitializeComponent();

    /// <param name="level">Current microphone level (0 to 1), or null when not in a voice channel.</param>
    internal VoiceSettingsDialog(AppSettings settings, Func<float>? level) : this()
    {
        _settings = settings;
        Fill(InputBox, VoiceAudio.InputNames(), settings.VoiceInput);
        Fill(OutputBox, VoiceAudio.OutputNames(), settings.VoiceOutput);
        OutputVolumeBox.Value = settings.VoiceOutputVolume;
        AlwaysBox.IsChecked = settings.VoiceAlwaysOn;
        PushToTalkBox.IsChecked = settings.VoicePushToTalk;
        _pushToTalkKey = settings.VoicePushToTalkKey;
        PushToTalkSoundBox.IsChecked = settings.VoicePushToTalkSound;
        ShowKey();
        KeyDown += OnKeyDown;
        AddHandler(PointerPressedEvent, OnPointerDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        NoiseBox.ItemsSource = Enum.GetNames<NoiseSuppression>();
        NoiseBox.SelectedItem = Enum.TryParse<NoiseSuppression>(settings.VoiceNoiseSuppression, out var noise) ? noise.ToString() : nameof(NoiseSuppression.Moderate);
        EchoBox.IsChecked = settings.VoiceEchoCancellation;
        if (!WebRtcVoiceProcessor.IsAvailable(out var unavailable))
        {
            EchoBox.IsEnabled = NoiseBox.IsEnabled = false;
            ProcessingNote.Text = "Not available on this system, so your microphone is sent as it is: " + unavailable;
        }
        SensitivityBox.Value = settings.VoiceSensitivity;
        if (level != null)
        {
            HintText.Text = "";
            _timer.Tick += (_, _) => LevelBar.Value = Math.Min(100, level() * 1000);
            _timer.Start();
        }
        Closed += (_, _) => _timer.Stop();
    }

    public bool Saved { get; private set; }

    private string? _pushToTalkKey;
    private bool _choosingKey;

    private void ShowKey() => PushToTalkKeyButton.Content = _choosingKey ? "Press a key or a mouse button… (Esc to cancel)"
        : PushToTalkTrigger.Parse(_pushToTalkKey) is not { } trigger ? "Choose a key or button…" : $"Hold: {trigger.Description} (click to change)";

    private void PushToTalkKey_Click(object? sender, RoutedEventArgs e)
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
        PushToTalkBox.IsChecked = true;
        ShowKey();
    }

    // The next key pressed becomes the push-to-talk key.
    private void OnKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (!_choosingKey) return;
        e.Handled = true;
        _choosingKey = false;
        if (e.Key != Avalonia.Input.Key.Escape) { _pushToTalkKey = e.Key.ToString(); PushToTalkBox.IsChecked = true; }
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

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        _settings.VoiceInput = Chosen(InputBox);
        _settings.VoiceOutput = Chosen(OutputBox);
        _settings.VoiceOutputVolume = (int)OutputVolumeBox.Value;
        if (PushToTalkBox.IsChecked == true && string.IsNullOrEmpty(_pushToTalkKey))
        {
            PushToTalkNote.Text = "Choose the key or mouse button to hold first.";
            return;
        }
        _settings.VoicePushToTalk = PushToTalkBox.IsChecked == true;
        _settings.VoicePushToTalkKey = _pushToTalkKey;
        _settings.VoicePushToTalkSound = PushToTalkSoundBox.IsChecked == true;
        _settings.VoiceAlwaysOn = AlwaysBox.IsChecked == true;
        _settings.VoiceEchoCancellation = EchoBox.IsChecked == true;
        _settings.VoiceNoiseSuppression = NoiseBox.SelectedItem as string ?? nameof(NoiseSuppression.Moderate);
        _settings.VoiceSensitivity = (int)SensitivityBox.Value;
        Saved = true;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
