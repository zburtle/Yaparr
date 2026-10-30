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
        new("Low: 640p, 8 pictures a second, about 0.4 Mbit/s", 1138, 640, 8, 400),
        new("Normal: 720p, 10 pictures a second, about 0.8 Mbit/s", 1280, 720, 10, 800),
        new("Sharp: 1080p, 12 pictures a second, about 1.6 Mbit/s", 1920, 1080, 12, 1600),
    };

    private readonly IReadOnlyList<Display> _displays = Displays.List();
    private IReadOnlyList<ShareWindow> _windows = Array.Empty<ShareWindow>();

    public ShareDialog()
    {
        InitializeComponent();
        FillChoices(selectFirst: true);
        foreach (var q in Qualities) QualityBox.Items.Add(q.Label);
        QualityBox.SelectedIndex = 1;
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
        var q = Qualities[Math.Max(0, QualityBox.SelectedIndex)];
        int what = WhatBox.SelectedIndex;
        int windowAt = what - _displays.Count;
        var window = windowAt >= 0 && windowAt < _windows.Count ? _windows[windowAt] : null;
        Result = new ScreenOptions
        {
            Display = what >= 0 && what < _displays.Count ? _displays[what] : null,
            WindowHandle = window?.Handle,
            MaxWidth = q.Width, MaxHeight = q.Height, Fps = q.Fps, Kbps = q.Kbps,
        };
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
