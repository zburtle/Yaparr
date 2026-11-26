using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Yaparr.AppCore;

namespace Yaparr.Desktop;

/// <summary>What was picked for playing a film in a room.</summary>
public sealed record FilmChoice(int? AudioIndex, int? SubtitleIndex, long StartTicks, int Kbps, int Height);

/// <summary>
/// Asks how to play a film or an episode in a room: which sound, whether to burn subtitles into the picture, where to start, and how sharp. The picture goes to everyone in the
/// room, so a quality that fits the upload speed is offered first.
/// </summary>
public sealed class FilmOptionsDialog : Window
{
    private readonly ComboBox _audio = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _subtitles = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _start = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _quality = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false };
    private readonly List<JellyfinTrack> _audioTracks, _subtitleTracks;
    private readonly long _resumeTicks;

    private static readonly (string Label, int Kbps, int Height)[] Qualities =
    {
        ("Smooth for a slow connection · 480p, 1.5 Mbit/s", 1500, 480),
        ("Standard · 720p, 3 Mbit/s", 3000, 720),
        ("Sharp · 1080p, 6 Mbit/s", 6000, 1080),
    };

    public FilmChoice? Result { get; private set; }

    private FilmOptionsDialog(string title, JellyfinPlaybackInfo info, long resumeTicks, double uploadBytesPerSecond, int viewersHint)
    {
        Title = "Play in the room";
        SizeToContent = SizeToContent.Height;
        Width = 480;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
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
        _subtitles.IsEnabled = _subtitleTracks.Count > 0;

        var starts = new List<string> { "From the beginning" };
        if (resumeTicks > TimeSpan.TicksPerMinute) starts.Add("Where you stopped, at " + TimeSpan.FromTicks(resumeTicks).ToString(@"h\:mm\:ss"));
        _start.ItemsSource = starts;
        _start.SelectedIndex = starts.Count > 1 ? 1 : 0;
        _start.SelectionChanged += (_, _) => Check();
        _subtitles.SelectionChanged += (_, _) => Check();

        _quality.ItemsSource = Qualities.Select(q => q.Label).ToList();
        int fit = uploadBytesPerSecond <= 0 ? 1 : uploadBytesPerSecond * 8 / 1000 / Math.Max(1, viewersHint) >= 7000 ? 2 : uploadBytesPerSecond * 8 / 1000 / Math.Max(1, viewersHint) >= 3500 ? 1 : 0;
        _quality.SelectedIndex = fit;

        var ok = new Button { Content = "Start playing", IsDefault = true, MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center, Classes = { "primary" } };
        ok.Click += (_, _) =>
        {
            var q = Qualities[Math.Max(0, _quality.SelectedIndex)];
            bool resume = _start.SelectedIndex == 1;
            int? sub = _subtitles.SelectedIndex > 0 && !resume ? _subtitleTracks[_subtitles.SelectedIndex - 1].Index : null;
            int? audio = _audioTracks.Count > 0 ? _audioTracks[Math.Max(0, _audio.SelectedIndex)].Index : null;
            Result = new FilmChoice(audio, sub, resume ? _resumeTicks : 0, q.Kbps, q.Height);
            Close(true);
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => Close(false);

        Control Row(string label, Control box) => new StackPanel { Spacing = 2, Margin = new Thickness(0, 6, 0, 0), Children = { new TextBlock { Text = label, Classes = { "muted" } }, box } };
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Everyone in the room can watch it. It is played from your Jellyfin server, so your upload speed is what the room gets.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
                Row("Sound", _audio),
                Row("Subtitles", _subtitles),
                Row("Start", _start),
                Row("Quality", _quality),
                _note,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 14, 0, 0), Children = { ok, cancel } },
            },
        };
        _note.Classes.Add("muted");
        Check();
    }

    // Subtitles in the picture and a start part-way in do not work together on the server (the sound would not belong to the picture), so one switches the other off.
    private void Check()
    {
        bool resume = _start.SelectedIndex == 1;
        _subtitles.IsEnabled = _subtitleTracks.Count > 0 && !resume;
        if (resume) _subtitles.SelectedIndex = 0;
        _note.IsVisible = resume && _subtitleTracks.Count > 0;
        _note.Text = "Subtitles can only be put into the picture when the film starts from the beginning.";
    }

    public static async Task<FilmChoice?> AskAsync(Window owner, string title, JellyfinPlaybackInfo info, long resumeTicks, double uploadBytesPerSecond, int viewersHint)
    {
        var dialog = new FilmOptionsDialog(title, info, resumeTicks, uploadBytesPerSecond, viewersHint);
        return await dialog.ShowDialog<bool>(owner) ? dialog.Result : null;
    }
}
