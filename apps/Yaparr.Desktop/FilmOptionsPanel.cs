using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Yaparr.AppCore;

namespace Yaparr.Desktop;

/// <summary>What was picked for playing a film in a room.</summary>
public sealed record FilmChoice(int? AudioIndex, int? SubtitleIndex, long StartTicks, int Kbps, int Height);

/// <summary>
/// The choices for playing a film or an episode in a room, as they sit in the Share window: which sound, whether to burn subtitles into the picture, where to start, and how
/// sharp. The picture goes to everyone in the room, so the quality that fits the upload speed is the one that starts picked.
/// </summary>
public sealed class FilmOptionsPanel : StackPanel
{
    private readonly ComboBox _audio = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _subtitles = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _start = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _quality = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false, Text = "Subtitles can only be put into the picture when the film starts from the beginning." };
    private readonly List<JellyfinTrack> _audioTracks, _subtitleTracks;
    private readonly long _resumeTicks;

    private static readonly (string Label, int Kbps, int Height)[] Qualities =
    {
        ("Smooth for a slow connection · 480p, 1.5 Mbit/s", 1500, 480),
        ("Standard · 720p, 3 Mbit/s", 3000, 720),
        ("Sharp · 1080p, 6 Mbit/s", 6000, 1080),
        ("Sharper · 1080p, 10 Mbit/s", 10000, 1080),
        ("Best · 1080p, 16 Mbit/s", 16000, 1080),
    };

    /// <summary>
    /// Which quality starts picked: the sharpest one that the upload speed carries for each viewer (a sixth more than the picture's bitrate, for the sound and the repairs of lost pieces),
    /// and Standard when the upload speed is not known.
    /// </summary>
    internal static int DefaultQuality(double uploadBytesPerSecond, int viewersHint)
    {
        if (uploadBytesPerSecond <= 0) return 1;
        double kbpsPerViewer = uploadBytesPerSecond * 8 / 1000 / Math.Max(1, viewersHint);
        int pick = 0;
        for (int i = 0; i < Qualities.Length; i++)
            if (Qualities[i].Kbps * 7.0 / 6 <= kbpsPerViewer) pick = i;
        return pick;
    }

    public FilmOptionsPanel(JellyfinPlaybackInfo info, long resumeTicks, double uploadBytesPerSecond, int viewersHint)
    {
        Spacing = 8;
        _resumeTicks = resumeTicks;
        _audioTracks = info.Audio.ToList();
        _subtitleTracks = info.Subtitles.ToList();

        _audio.ItemsSource = _audioTracks.Count == 0 ? new[] { "The film's own sound" } : _audioTracks.Select(t => t.Title + (t.IsDefault ? " (default)" : "")).ToList();
        _audio.SelectedIndex = Math.Max(0, _audioTracks.FindIndex(t => t.IsDefault));
        _audio.IsEnabled = _audioTracks.Count > 1;

        var subs = new List<string> { "None" };
        subs.AddRange(_subtitleTracks.Select(t => t.Title + (t.IsText ? "" : " (picture, slower)")));
        _subtitles.ItemsSource = subs;
        _subtitles.SelectedIndex = 0;

        var starts = new List<string> { "From the beginning" };
        if (resumeTicks > TimeSpan.TicksPerMinute) starts.Add("Where you stopped, at " + TimeSpan.FromTicks(resumeTicks).ToString(@"h\:mm\:ss"));
        _start.ItemsSource = starts;
        _start.SelectedIndex = starts.Count > 1 ? 1 : 0;
        _start.SelectionChanged += (_, _) => Check();

        _quality.ItemsSource = Qualities.Select(q => q.Label).ToList();
        _quality.SelectedIndex = DefaultQuality(uploadBytesPerSecond, viewersHint);

        Control Row(string label, Control box)
        {
            var text = new TextBlock { Text = label, FontSize = 12 };
            text.Classes.Add("muted");
            return new StackPanel { Spacing = 2, Children = { text, box } };
        }
        _note.Classes.Add("muted");
        Children.Add(Row("Sound", _audio));
        Children.Add(Row("Subtitles", _subtitles));
        Children.Add(Row("Start", _start));
        Children.Add(Row("Quality", _quality));
        Children.Add(_note);
        Check();
    }

    // Subtitles in the picture and a start part-way in do not work together on the server (the sound would not belong to the picture), so one switches the other off.
    private void Check()
    {
        bool resume = _start.SelectedIndex == 1;
        _subtitles.IsEnabled = _subtitleTracks.Count > 0 && !resume;
        if (resume) _subtitles.SelectedIndex = 0;
        _note.IsVisible = resume && _subtitleTracks.Count > 0;
    }

    public FilmChoice Choice
    {
        get
        {
            var q = Qualities[Math.Max(0, _quality.SelectedIndex)];
            bool resume = _start.SelectedIndex == 1;
            int? sub = _subtitles.SelectedIndex > 0 && !resume ? _subtitleTracks[_subtitles.SelectedIndex - 1].Index : null;
            int? audio = _audioTracks.Count > 0 ? _audioTracks[Math.Max(0, _audio.SelectedIndex)].Index : null;
            return new FilmChoice(audio, sub, resume ? _resumeTicks : 0, q.Kbps, q.Height);
        }
    }
}
