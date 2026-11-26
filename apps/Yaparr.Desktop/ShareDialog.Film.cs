using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Yaparr.AppCore;

namespace Yaparr.Desktop;

/// <summary>A Jellyfin server that is signed in, as the Share window is offered it: a way to make a client (the window makes its own and lets go of them) and the server's name.</summary>
public sealed record JellyfinOffer(Func<JellyfinClient> NewClient, string ServerName, double UploadBytesPerSecond);

/// <summary>A film or an episode picked in the Share window, with what the server says about it and how it is to be played.</summary>
public sealed record FilmPlay(JellyfinItem Item, JellyfinPlaybackInfo Info, FilmChoice Choice, string Title);

// The Jellyfin side of the Share window: pick something to watch (what was started and not finished, or a look through the library), then how to play it.
public partial class ShareDialog
{
    private JellyfinOffer? _offer;
    private JellyfinClient? _filmClient;
    private readonly Dictionary<string, Bitmap> _posters = new();
    private JellyfinItem? _picked;
    private JellyfinPlaybackInfo? _pickedInfo;
    private FilmOptionsPanel? _options;
    private bool _filmPageBuilt;
    private bool _closed;

    /// <summary>The film picked, when the window was answered with the Jellyfin card; null for a screen or a window (see <see cref="Result"/>).</summary>
    public FilmPlay? Film { get; private set; }

    /// <summary>Adds the Jellyfin card next to the screen one. Call it before the window is shown.</summary>
    public void OfferFilm(JellyfinOffer offer)
    {
        _offer = offer;
        _filmClient = offer.NewClient();
        FilmCardText.Text = offer.ServerName.Length > 0 ? offer.ServerName : "A film or show from your server";
        SourceCards.IsVisible = true;
        ScreenCard.IsCheckedChanged += (_, _) => ShowSource();
        FilmCard.IsCheckedChanged += (_, _) => ShowSource();
        Closed += (_, _) =>
        {
            _closed = true;
            _filmClient?.Dispose();
            foreach (var bitmap in _posters.Values) bitmap.Dispose();
            _posters.Clear();
        };
    }

    private void ShowSource()
    {
        bool film = FilmCard.IsChecked == true;
        ScreenPanel.IsVisible = !film;
        FilmPanel.IsVisible = film;
        HeadIcon.Text = film ? "🎬" : "🖥";
        HeadSub.Text = film ? "Play a film or an episode from your Jellyfin server." : "Everyone in the room can watch.";
        Note.Text = film ? "It is played from your server, so your upload speed is what the room gets." : "Higher quality needs a faster upload.";
        LearnMore.IsVisible = !film;
        ShareButton.Content = film ? "Play" : "Share";
        ShareButton.IsEnabled = !film || _pickedInfo != null;
        if (film && !_filmPageBuilt) { _filmPageBuilt = true; ShowPickPage(); }
    }

    private static TextBlock Muted(string text, double size = 12)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        t.Classes.Add("muted");
        return t;
    }

    private static TextBlock Section(string text)
    {
        var t = new TextBlock { Text = text };
        t.Classes.Add("section");
        t.Classes.Add("first");
        return t;
    }

    // ---- what to watch ----

    private void ShowPickPage()
    {
        _picked = null;
        _pickedInfo = null;
        _options = null;
        ShareButton.IsEnabled = false;
        FilmBody.Children.Clear();

        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var status = Muted("Looking for what you were watching…");
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = strip };
        var browse = new Button { Content = "Browse your library…", HorizontalAlignment = HorizontalAlignment.Left, Classes = { "primary" }, Margin = new Thickness(0, 4, 0, 0) };
        browse.Click += async (_, _) => await BrowseAsync(status);

        FilmBody.Children.Add(Section("CONTINUE WATCHING"));
        FilmBody.Children.Add(scroll);
        FilmBody.Children.Add(status);
        FilmBody.Children.Add(browse);
        _ = FillResumeAsync(strip, status);
    }

    private async Task FillResumeAsync(StackPanel strip, TextBlock status)
    {
        try
        {
            var page = await _filmClient!.GetResumeAsync(10);
            if (_closed) return;
            foreach (var item in page.Items) strip.Children.Add(Tile(item));
            status.Text = page.Items.Count == 0 ? "Nothing started yet. Browse your library to pick something." : "";
            status.IsVisible = status.Text.Length > 0;
        }
        catch (Exception ex) when (!_closed)
        {
            status.Text = ex is JellyfinException { Unauthorized: true } ? "Jellyfin no longer accepts the sign-in. Sign in again in the settings." : ex is HttpRequestException or TaskCanceledException ? "The server could not be reached." : ex.Message;
        }
    }

    private Control Tile(JellyfinItem item)
    {
        var poster = new Border { Width = 92, Height = 138, CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = (IBrush?)(this.TryFindResource("Hover", ActualThemeVariant, out var hover) ? hover : null) };
        poster.Child = new TextBlock { Text = item.Name.Length > 0 ? item.Name[..1].ToUpperInvariant() : "?", FontSize = 28, Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _ = LoadPosterAsync(item, poster, 92 * 2);
        var title = new TextBlock { Text = item.Name, FontSize = 12, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 92 };
        var sub = Muted(item.Subtitle, 11);
        sub.TextTrimming = TextTrimming.CharacterEllipsis;
        sub.TextWrapping = TextWrapping.NoWrap;
        sub.MaxWidth = 92;
        var button = new Button { Content = new StackPanel { Spacing = 2, Children = { poster, title, sub } }, Classes = { "ghost" }, Padding = new Thickness(4), HorizontalContentAlignment = HorizontalAlignment.Left };
        Avalonia.Automation.AutomationProperties.SetName(button, item.Name + (item.Subtitle.Length > 0 ? ", " + item.Subtitle : ""));
        button.Click += async (_, _) => await PickAsync(item);
        return button;
    }

    private async Task LoadPosterAsync(JellyfinItem item, Border into, int width)
    {
        try
        {
            if (!_posters.TryGetValue(item.Id, out var bitmap))
            {
                var bytes = await _filmClient!.GetPosterAsync(item.Id, item.ImageTag, width);
                if (bytes == null || _closed) return;
                using var stream = new MemoryStream(bytes);
                bitmap = Bitmap.DecodeToWidth(stream, width);
                _posters[item.Id] = bitmap;
            }
            into.Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }   // the letter stays
    }

    private async Task BrowseAsync(TextBlock status)
    {
        if (_offer == null) return;
        JellyfinWindow? window = null;
        window = new JellyfinWindow(_offer.NewClient(), _offer.ServerName,
            signedOut: () => { status.Text = "Jellyfin signed you out. Sign in again in the settings."; status.IsVisible = true; },
            play: async item =>
            {
                window?.Close();
                await PickAsync(item);
            },
            playLabel: "Choose this");
        await window.ShowDialog(this);
    }

    // ---- how to play it ----

    private async Task PickAsync(JellyfinItem item)
    {
        FilmBody.Children.Clear();
        FilmBody.Children.Add(Muted("Getting the sound and subtitle tracks…"));
        ShareButton.IsEnabled = false;
        try
        {
            var info = await _filmClient!.GetPlaybackInfoAsync(item.Id);
            if (_closed) return;
            _picked = item;
            _pickedInfo = info;
            long resume = item.PlayedPercent is > 1 and < 95 && item.RuntimeTicks > 0 ? (long)(item.RuntimeTicks * item.PlayedPercent / 100) : 0;
            _options = new FilmOptionsPanel(info, resume, _offer?.UploadBytesPerSecond ?? 0, viewersHint: 2);
            ShowPickedPage(item, info);
            ShareButton.IsEnabled = true;
        }
        catch (Exception ex) when (!_closed)
        {
            ShowPickPage();
            FilmBody.Children.Add(new TextBlock { Text = ex is JellyfinException { Unauthorized: true } ? "Jellyfin no longer accepts the sign-in. Sign in again in the settings." : "Could not get that film: " + ex.Message, TextWrapping = TextWrapping.Wrap, Foreground = this.TryFindResource("Bad", ActualThemeVariant, out var bad) && bad is IBrush brush ? brush : Brushes.Red });
        }
    }

    private void ShowPickedPage(JellyfinItem item, JellyfinPlaybackInfo info)
    {
        FilmBody.Children.Clear();
        var poster = new Border { Width = 64, Height = 96, CornerRadius = new CornerRadius(6), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Top, Background = (IBrush?)(this.TryFindResource("Hover", ActualThemeVariant, out var hover) ? hover : null) };
        poster.Child = new TextBlock { Text = item.Name.Length > 0 ? item.Name[..1].ToUpperInvariant() : "?", FontSize = 24, Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _ = LoadPosterAsync(item, poster, 128);

        var details = new List<string>();
        if (item.Subtitle.Length > 0) details.Add(item.Subtitle);
        int minutes = info.RunTimeTicks > 0 ? (int)(info.RunTimeTicks / TimeSpan.TicksPerMinute) : item.RuntimeMinutes;
        if (minutes > 0) details.Add(minutes >= 60 ? $"{minutes / 60} h {minutes % 60:00} min" : $"{minutes} min");
        var change = new Button { Content = "Choose something else", Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0, 4), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = this.TryFindResource("Accent", ActualThemeVariant, out var accent) && accent is IBrush a ? a : null };
        change.Click += (_, _) => ShowPickPage();
        var text = new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = item.Kind == JellyfinKind.Episode && item.SeriesName.Length > 0 ? item.SeriesName + ": " + item.Name : item.Name, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                Muted(string.Join("  ·  ", details)),
                change,
            },
        };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 0, 0, 4) };
        text.Margin = new Thickness(14, 0, 0, 0);
        Grid.SetColumn(text, 1);
        head.Children.Add(poster);
        head.Children.Add(text);
        FilmBody.Children.Add(head);
        FilmBody.Children.Add(_options!);
    }

    // Called when the answer is Play: what is picked and how it is to be played.
    private bool TakeFilm()
    {
        if (_picked == null || _pickedInfo == null || _options == null) return false;
        var title = _picked.Kind == JellyfinKind.Episode && _picked.SeriesName.Length > 0 ? $"{_picked.SeriesName}: {_picked.Name}" : _picked.Name;
        Film = new FilmPlay(_picked, _pickedInfo, _options.Choice, title);
        return true;
    }
}
