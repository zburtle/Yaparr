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
    public ShareDialog() : this(null) { }

    public ShareDialog(ScreenOptions? current)
    {
        InitializeComponent();
        _current = current;
        // The note about covered and minimised windows is about Windows Graphics Capture, and the sound is shared only where a
        // program's own sound can be captured.
        WindowsNote.IsVisible = OperatingSystem.IsWindows() || ScreenCapture.UsesPortal;
        if (ScreenCapture.UsesPortal)
        {
            WindowsNote.Text = "The desktop will ask which monitor or window to share, and shows its own picker.";
            RefreshButton.IsVisible = false;
        }
        else if (OperatingSystem.IsLinux() && X11Windows.MissingTools() is { } missing)
        {
            WindowsNote.Text = missing;
            WindowsNote.IsVisible = true;
        }
        SoundNote.IsVisible = ScreenSound.IsSupported;
        if (!OperatingSystem.IsWindows()) SoundNote.Text = ScreenCapture.UsesPortal
            ? "The sound of your computer is shared too, except Yaparr's own, so the voice chat does not go round. Wayland does not say which program a window belongs to, so to share only one program's sound, pick it below (it must be playing now)."
            : "The sound of a shared window is shared too (not the voice chat, which is another program); a whole monitor shares the sound of your computer, except Yaparr's own.";
        if (ScreenCapture.UsesPortal && ScreenSound.IsSupported)
        {
            SoundBox.IsVisible = true;
            FillSoundChoices();
        }
        if (current != null)
        {
            Title = "Change what is shared";
            QualityLabel.IsVisible = QualityBox.IsVisible = SoundNote.IsVisible = SoundBox.IsVisible = Note.IsVisible = false;
            ShareButton.Content = "Switch";
        }
        FillChoices(selectFirst: true);
        foreach (var q in Qualities) QualityBox.Items.Add(q.Label);
        QualityBox.SelectedIndex = 0;
    }

    // The monitors first, then the windows.
    private void FillChoices(bool selectFirst)
    {
        if (ScreenCapture.UsesPortal)
        {
            // Under Wayland a program cannot list monitors or windows; it can only say which kind it wants from the desktop's picker.
            WhatBox.Items.Clear();
            WhatBox.Items.Add("A monitor");
            WhatBox.Items.Add("A window");
            WhatBox.SelectedIndex = 0;
            return;
        }
        var keep = (WhatBox.SelectedItem as string, _windows);
        _windows = ShareWindows.List(Environment.ProcessId);
        WhatBox.Items.Clear();
        foreach (var d in _displays)
            WhatBox.Items.Add($"Monitor {d.Index + 1}{(d.Primary ? " (main)" : "")}: {d.Width} x {d.Height}");
        foreach (var w in _windows) WhatBox.Items.Add("Window: " + w.Label);
        WhatBox.SelectedIndex = selectFirst ? 0 : Math.Max(0, WhatBox.Items.IndexOf(keep.Item1));
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e) => FillChoices(selectFirst: false);

    // Under Wayland: everything except Yaparr, or one of the programs that are playing sound now.
    private const string EverythingElse = "Sound: everything except Yaparr";

    private void FillSoundChoices()
    {
        var keep = SoundBox.SelectedItem as string;
        SoundBox.Items.Clear();
        SoundBox.Items.Add(EverythingElse);
        var programs = PulseAppCapture.ListPlayingPrograms();
        foreach (var name in programs) SoundBox.Items.Add("Sound: only " + name);
        // A program picked before stays picked even if it has gone quiet since.
        if (keep != null && keep != EverythingElse && !SoundBox.Items.Contains(keep)) SoundBox.Items.Add(keep);
        SoundBox.SelectedItem = keep ?? EverythingElse;
        if (SoundBox.SelectedItem == null) SoundBox.SelectedIndex = 0;
    }

    private void SoundBox_DropDownOpened(object? sender, EventArgs e) => FillSoundChoices();

    private string? PickedProgram() =>
        SoundBox.IsVisible && SoundBox.SelectedItem is string s && s.StartsWith("Sound: only ", StringComparison.Ordinal) ? s["Sound: only ".Length..] : null;

    public ScreenOptions? Result { get; private set; }

    /// <summary>Under Wayland: whether the desktop's picker should offer windows rather than monitors.</summary>
    public bool PortalWindow { get; private set; }

    private void Share_Click(object? sender, RoutedEventArgs e)
    {
        int what = WhatBox.SelectedIndex;
        PortalWindow = ScreenCapture.UsesPortal && what == 1;
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
            ShareSound = ScreenSound.IsSupported,
            SoundProgram = PickedProgram(),
        };
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
