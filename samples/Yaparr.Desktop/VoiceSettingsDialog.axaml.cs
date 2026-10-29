using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

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
        AlwaysBox.IsChecked = settings.VoiceAlwaysOn;
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
        _settings.VoiceAlwaysOn = AlwaysBox.IsChecked == true;
        _settings.VoiceSensitivity = (int)SensitivityBox.Value;
        Saved = true;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
