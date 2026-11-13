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
    public ShareDialog() : this(null, null) { }

    /// <summary>The upload speed Yaparr measured for this connection (see <see cref="Yaparr.Chat.ChatNode.MeasuredUploadBytesPerSecond"/>), if it has.</summary>
    private readonly long? _upload;

    /// <summary>With <paramref name="upload"/>, the dialog says what this connection suits and starts at the best quality that can be sent to three people at once.</summary>
    public ShareDialog(long? upload) : this(null, upload) { }

    public ShareDialog(ScreenOptions? current, long? upload = null)
    {
        InitializeComponent();
        _current = current;
        _upload = upload;
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
            QualityLabel.IsVisible = QualityBox.IsVisible = FormatLabel.IsVisible = FormatBox.IsVisible = AdaptBox.IsVisible = AdaptHint.IsVisible = SoundNote.IsVisible = SoundBox.IsVisible = Note.IsVisible = false;
            ShareButton.Content = "Switch";
        }
        ApplyAv1Check();
        FillChoices(selectFirst: true);
        foreach (var q in Qualities) QualityBox.Items.Add(q.Label);
        // The best quality that this connection can send to three people at once, or the lowest when none can (or the upload is not known yet).
        int best = 0;
        if (_upload != null)
            for (int i = 0; i < Qualities.Length; i++)
                if (Yaparr.Chat.ChatNode.DirectViewersAt(_upload, Qualities[i].Kbps) >= 3) best = i;
        QualityBox.SelectedIndex = best;
    }

    // When the start-up check found that no graphics card here makes AV1, it is not offered: choosing it would only end in H.264 anyway. While the check is
    // still running (or FFmpeg was only just downloaded) it stays available.
    private void ApplyAv1Check()
    {
        if (Av1Support.Latest is not { CanEncode: false } report) return;
        if (FormatBox.Items[1] is ComboBoxItem item) { item.IsEnabled = false; item.Content = "AV1 (no graphics card here can make it)"; }
        FormatHint.Text = "This computer's graphics card cannot make AV1 live" + (report.Refused.Count > 0 ? $" ({string.Join("; ", report.Refused.Select(r => r.Value))})" : "") + ". Sharing uses H.264.";
        FormatHint.IsVisible = true;
    }

    /// <summary>Starts with the video format picked last time (AV1 only if it is on offer here).</summary>
    public void PreferAv1(bool prefer)
    {
        if (prefer && FormatBox.Items[1] is ComboBoxItem { IsEnabled: true }) FormatBox.SelectedIndex = 1;
    }

    /// <summary>Starts with the last choice about lowering the quality for viewers that cannot keep up.</summary>
    public void PreferAdaptQuality(bool adapt) => AdaptBox.IsChecked = adapt;

    /// <summary>Whether lowering the quality was left on in the dialog that was just answered.</summary>
    public bool AdaptPicked => AdaptBox.IsChecked == true;

    /// <summary>Whether AV1 was picked in the dialog that was just answered.</summary>
    public bool Av1Picked => Result?.Codec == ScreenCodec.Av1;

    private void FormatBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Raised while the window is still being built (the box starts with its first choice selected), before the hint exists.
        if (FormatHint != null && Av1Support.Latest is not { CanEncode: false }) FormatHint.IsVisible = FormatBox.SelectedIndex == 1;
    }

    private void QualityBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_upload is not { } upload || QualityBox.SelectedIndex < 0) { QualityHint.IsVisible = false; return; }
        var q = Qualities[QualityBox.SelectedIndex];
        int viewers = Yaparr.Chat.ChatNode.DirectViewersAt(upload, q.Kbps);
        string speed = $"{upload * 8 / 1_000_000.0:0} Mbit/s";
        QualityHint.IsVisible = true;
        QualityHint.Text = viewers >= 3
            ? $"Your connection sends about {speed}, enough to send this directly to {(viewers >= 6 ? "six" : viewers.ToString())} people at once; anyone else watches through them."
            : $"Your connection sends about {speed}, which suits sending this to {viewers} {(viewers == 1 ? "person" : "people")} at once, and the rest watch through them. A lower quality keeps the picture and the voice chat steadier for everyone.";
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
        // On an X11 session with the desktop's portal: one more way to share a window, which follows it when it is resized or covered (x11grab stops when it gets smaller).
        _portalChoiceAt = -1;
        if (OperatingSystem.IsLinux() && ScreenCapture.OffersPortalWindowOnX11)
        {
            _portalChoiceAt = WhatBox.Items.Count;
            WhatBox.Items.Add(PortalWindowChoice);
        }
        WhatBox.SelectedIndex = selectFirst ? 0 : Math.Max(0, WhatBox.Items.IndexOf(keep.Item1));
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e) => FillChoices(selectFirst: false);

    private const string PortalWindowChoice = "A window, chosen in the desktop's picker (keeps up when it is resized)";
    private int _portalChoiceAt = -1;

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

    /// <summary>Whether the desktop's picker should be asked for a window: under Wayland, when "A window" was chosen; under X11, when the window chosen in the desktop's picker was.</summary>
    public bool PortalWindow { get; private set; }

    private void Share_Click(object? sender, RoutedEventArgs e)
    {
        int what = WhatBox.SelectedIndex;
        PortalWindow = ScreenCapture.UsesPortal ? what == 1 : _portalChoiceAt >= 0 && what == _portalChoiceAt;
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
            Codec = FormatBox.SelectedIndex == 1 ? ScreenCodec.Av1 : ScreenCodec.Auto,
            ShareSound = ScreenSound.IsSupported,
            SoundProgram = PickedProgram(),
            AdaptQuality = AdaptBox.IsChecked == true && new ScreenOptions().AdaptQuality,
        };
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
