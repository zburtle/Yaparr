using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Yaparr.Chat;

namespace Yaparr.Desktop;

/// <summary>Picks a sound file, names it, and (for a long one) picks the 20 seconds to keep.</summary>
public partial class AddSoundDialog : Window
{
    private float[] _samples = Array.Empty<float>();
    private int _rate = 48000, _channels = 1;
    private double _seconds;
    private readonly Func<string?> _output = () => null;

    public AddSoundDialog() => InitializeComponent();

    internal AddSoundDialog(Func<string?> output) : this() => _output = output;

    public string? SoundName { get; private set; }
    public SoundClip? Clip { get; private set; }

    private async void Choose_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a sound",
            AllowMultiple = false,
            FileTypeFilter = new[] { SoundImport.Sounds },
        });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            if (ms.Length > 64 * 1024 * 1024) throw new ArgumentException("That file is too big.");
            var bytes = ms.ToArray();
            (_samples, _rate, _channels) = await Task.Run(() => SoundImport.Decode(bytes));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            InfoText.Text = ex.Message;
            PreviewButton.IsEnabled = AddButton.IsEnabled = false;
            return;
        }

        _seconds = (double)_samples.Length / _channels / _rate;
        FileText.Text = files[0].Name;
        if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = Path.GetFileNameWithoutExtension(files[0].Name);
        TrimPanel.IsVisible = _seconds > SoundClip.MaxSeconds;
        StartBox.Maximum = Math.Max(0.1, _seconds - SoundClip.MaxSeconds);
        StartBox.Value = 0;
        PreviewButton.IsEnabled = AddButton.IsEnabled = true;
        ShowInfo();
    }

    private void Start_Changed(object? sender, RangeBaseValueChangedEventArgs e) => ShowInfo();

    private void ShowInfo()
    {
        if (_samples.Length == 0) return;
        double start = TrimPanel.IsVisible ? StartBox.Value : 0;
        double length = Math.Min(SoundClip.MaxSeconds, _seconds - start);
        InfoText.Text = TrimPanel.IsVisible
            ? $"This file is {_seconds:0.#} seconds, more than the 20 a sound can be. Using {length:0.#} seconds from {start:0.#} s."
            : $"{_seconds:0.#} seconds.";
    }

    // The part of the file that will become the sound.
    private SoundClip? Build()
    {
        if (_samples.Length == 0) return null;
        try
        {
            double start = TrimPanel.IsVisible ? StartBox.Value : 0;
            int first = (int)(start * _rate) * _channels;
            int count = (int)(Math.Min(SoundClip.MaxSeconds, _seconds - start) * _rate) * _channels;
            count = Math.Min(count, _samples.Length - first);
            return SoundClip.FromPcm(_samples.AsSpan(first, count), _rate, _channels);
        }
        catch (ArgumentException ex)
        {
            InfoText.Text = ex.Message;
            return null;
        }
    }

    private void Preview_Click(object? sender, RoutedEventArgs e)
    {
        if (Build() is not { } clip) return;
        try { VoiceAudio.PlayOnce(clip.ToSamples(), _output()); }
        catch (Exception ex) { InfoText.Text = "Couldn't play the sound: " + ex.Message; }
    }

    private void Add_Click(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (name.Length == 0) { InfoText.Text = "Give the sound a name."; NameBox.Focus(); return; }
        if (Build() is not { } clip) return;
        SoundName = name;
        Clip = clip;
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
