using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaparr.Screen;

namespace Yaparr.Desktop;

/// <summary>Asks which monitor to share and how sharp it should be.</summary>
public partial class ShareDialog : Window
{
    private sealed record Quality(string Label, int Width, int Height, int Fps, int Kbps);

    private static readonly Quality[] Qualities =
    {
        new("720p, 30 FPS", 1280, 720, 30, 2000),
        new("1080p, 30 FPS", 1920, 1080, 30, 4000),
        new("1080p, 60 FPS", 1920, 1080, 60, 6000),
    };

    private readonly IReadOnlyList<Display> _displays = Displays.List();
    private IReadOnlyList<ShareWindow> _windows = Array.Empty<ShareWindow>();

    private readonly ScreenOptions? _current;

    /// <summary>With <paramref name="current"/>, picks another monitor or window for a share that is running: quality and sound stay as they are.</summary>
    public ShareDialog(ScreenOptions? current = null)
    {
        InitializeComponent();
        _current = current;
        if (current != null)
        {
            Title = "Change what is shared";
            QualityLabel.IsVisible = QualityBox.IsVisible = SoundNote.IsVisible = Note.IsVisible = false;
            ShareButton.Content = "Switch";
        }
        FillChoices(selectFirst: true);
        foreach (var q in Qualities) QualityBox.Items.Add(q.Label);
        QualityBox.SelectedIndex = 0;
    }

    // The monitors first, then the windows.
    private void FillChoices(bool selectFirst)
    {
        var keep = (WhatBox.SelectedItem as string, _windows);
        _windows = ShareWindows.List(Environment.ProcessId);
        WhatBox.Items.Clear();
        foreach (var d in _displays)
            WhatBox.Items.Add($"Monitor {d.Index + 1}{(d.Primary ? " (main)" : "")}: {d.Width} x {d.Height}");
        foreach (var w in _windows) WhatBox.Items.Add("Window: " + w.Label);
        WhatBox.SelectedIndex = selectFirst ? 0 : Math.Max(0, WhatBox.Items.IndexOf(keep.Item1));
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e) => FillChoices(selectFirst: false);

    public ScreenOptions? Result { get; private set; }

    private void Share_Click(object? sender, RoutedEventArgs e)
    {
        int what = WhatBox.SelectedIndex;
        int windowAt = what - _displays.Count;
        var window = windowAt >= 0 && windowAt < _windows.Count ? _windows[windowAt] : null;
        if (_current != null)
        {
            Result = _current.Retarget(what >= 0 && what < _displays.Count ? _displays[what] : null, window?.Handle);
            Close(true);
            return;
        }
        var q = Qualities[Math.Max(0, QualityBox.SelectedIndex)];
        Result = new ScreenOptions
        {
            Display = what >= 0 && what < _displays.Count ? _displays[what] : null,
            WindowHandle = window?.Handle,
            MaxWidth = q.Width, MaxHeight = q.Height, Fps = q.Fps, Kbps = q.Kbps,
            ShareSound = true,
        };
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
