using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Yaparr.AppCore;

namespace Yaparr.Desktop;

/// <summary>
/// Looking through a Jellyfin library: what was started, the movies and shows of each library, a search, and for a show its seasons and episodes. Picking something
/// shows what the server knows about it. (Streaming it to a room is not here yet.) Opened from the Jellyfin section of the settings; it talks to the server from
/// this computer only (see <see cref="JellyfinClient"/>).
/// </summary>
public sealed class JellyfinWindow : Window
{
    private const int PageSize = 60;
    private const int PosterWidth = 130, PosterHeight = 195;

    private readonly JellyfinClient _client;
    private readonly Action _signedOut;
    private readonly Func<JellyfinItem, Task>? _play;
    private JellyfinItem? _shown;
    private readonly Button _playButton = new() { Content = "Choose this", Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), IsVisible = false };
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, Bitmap> _posters = new();

    private readonly ComboBox _source = new() { MinWidth = 180 };
    private readonly TextBox _search = new() { PlaceholderText = "Search movies and shows", MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly WrapPanel _tiles = new() { Orientation = Orientation.Horizontal };
    private readonly Button _more = new() { Content = "Show more", HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false, Margin = new Thickness(0, 8) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8) };
    private readonly Grid _gridView = new() { RowDefinitions = new RowDefinitions("*") };
    private readonly Grid _seriesView = new() { RowDefinitions = new RowDefinitions("Auto,*"), IsVisible = false };
    private readonly TextBlock _seriesTitle = new() { FontSize = 17, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _seasons = new() { MinWidth = 160 };
    private readonly ListBox _episodes = new();
    private readonly TextBlock _detailTitle = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detailLine = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detailText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border _detail = new() { Padding = new Thickness(12, 8), IsVisible = false };

    private IReadOnlyList<JellyfinLibrary> _libraries = Array.Empty<JellyfinLibrary>();
    private List<JellyfinItem> _listed = new();
    private int _total;
    private int _generation;
    private string? _seriesId;
    private bool _closing;

    public JellyfinWindow(JellyfinClient client, string serverName, Action signedOut, Func<JellyfinItem, Task>? play = null, string playLabel = "Choose this")
    {
        _playButton.Content = playLabel;
        _client = client;
        _signedOut = signedOut;
        _play = play;
        Title = serverName.Length > 0 ? "Jellyfin · " + serverName : "Jellyfin";
        Width = 920;
        Height = 700;
        MinWidth = 480;
        MinHeight = 400;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = new DynamicResourceExtension("AppBg");
        _status.Classes.Add("muted");
        _detailLine.Classes.Add("muted");
        _detail[!Border.BackgroundProperty] = new DynamicResourceExtension("Hover");

        _search.KeyDown += (_, e) => { if (e.Key == Key.Enter) _ = LoadAsync(); };
        _source.SelectionChanged += (_, _) => { if (_source.SelectedIndex >= 0 && !_closing) { _search.Text = ""; _ = LoadAsync(); } };
        _more.Click += async (_, _) => await LoadMoreAsync();
        var searchButton = new Button { Content = "Search" };
        searchButton.Click += (_, _) => _ = LoadAsync();

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(12, 12, 12, 6) };
        _source.Margin = new Thickness(0, 0, 8, 0);
        searchButton.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_search, 1);
        Grid.SetColumn(searchButton, 2);
        top.Children.Add(_source);
        top.Children.Add(_search);
        top.Children.Add(searchButton);

        var tilesHost = new StackPanel { Children = { _tiles, _more } };
        _gridView.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new Border { Padding = new Thickness(12, 0), Child = tilesHost } });

        var back = new Button { Content = "← Back" };
        back.Click += (_, _) => ShowGrid();
        _seasons.SelectionChanged += async (_, _) => await LoadEpisodesAsync();
        _episodes.SelectionChanged += (_, _) => { if (_episodes.SelectedItem is EpisodeRow row) ShowDetail(row.Item); };
        var seriesTop = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(12, 0, 12, 6), Children = { back, _seriesTitle, _seasons } };
        _seriesView.Children.Add(seriesTop);
        _episodes.Margin = new Thickness(12, 0);
        Grid.SetRow(_episodes, 1);
        _seriesView.Children.Add(_episodes);

        _playButton.Click += async (_, _) => { if (_shown is { } item && _play != null) await _play(item); };
        _detail.Child = new StackPanel { Spacing = 2, Children = { _detailTitle, _detailLine, _detailText, _playButton } };

        var content = new Panel { Children = { _gridView, _seriesView } };
        var rows = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        Grid.SetRow(_status, 1);
        _status.Margin = new Thickness(12, 0);
        Grid.SetRow(content, 2);
        Grid.SetRow(_detail, 3);
        rows.Children.Add(top);
        rows.Children.Add(_status);
        rows.Children.Add(content);
        rows.Children.Add(_detail);
        Content = rows;

        Opened += async (_, _) => await StartAsync();
        Closed += (_, _) =>
        {
            _closing = true;
            _cts.Cancel();
            foreach (var bitmap in _posters.Values) bitmap.Dispose();
            _posters.Clear();
            _client.Dispose();
        };
    }

    private sealed record EpisodeRow(JellyfinItem Item)
    {
        public override string ToString() => (Item.Number is { } n ? n + ". " : "") + Item.Name + (Item.RuntimeMinutes > 0 ? $"   ·   {Item.RuntimeMinutes} min" : "") + (Item.PlayedPercent >= 90 ? "   ·   watched" : "");
    }

    private async Task StartAsync()
    {
        Say("Loading…");
        try
        {
            _libraries = await _client.GetLibrariesAsync(_cts.Token);
            var names = new List<string> { "Continue watching" };
            names.AddRange(_libraries.Select(l => l.Name));
            _source.ItemsSource = names;
            _source.SelectedIndex = _libraries.Count > 0 ? 1 : 0;   // triggers the first load
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void Say(string text)
    {
        _status.Text = text;
        _status.IsVisible = text.Length > 0;
    }

    private void Fail(Exception ex)
    {
        if (ex is OperationCanceledException && _closing) return;
        if (ex is JellyfinException { Unauthorized: true })
        {
            _signedOut();
            Close();
            return;
        }
        Say(ex is HttpRequestException or TaskCanceledException ? "The server could not be reached." : ex.Message);
    }

    // ---- the list of movies and shows ----

    private async Task LoadAsync()
    {
        int generation = ++_generation;
        ShowGrid();
        _tiles.Children.Clear();
        _listed = new();
        _more.IsVisible = false;
        Say("Loading…");
        try
        {
            var search = _search.Text?.Trim();
            var index = _source.SelectedIndex;
            JellyfinPage page;
            if (!string.IsNullOrEmpty(search)) page = await _client.GetItemsAsync(null, search, 0, PageSize, _cts.Token);
            else if (index <= 0) page = await _client.GetResumeAsync(ct: _cts.Token);
            else page = await _client.GetItemsAsync(_libraries[index - 1].Id, null, 0, PageSize, _cts.Token);
            if (generation != _generation) return;
            _total = page.Total;
            AddTiles(page.Items);
            Say(page.Items.Count == 0 ? (!string.IsNullOrEmpty(search) ? "Nothing matches that." : index <= 0 ? "Nothing to continue watching." : "This library is empty.") : "");
        }
        catch (Exception ex) { if (generation == _generation) Fail(ex); }
    }

    private async Task LoadMoreAsync()
    {
        int generation = _generation;
        _more.IsEnabled = false;
        try
        {
            var search = _search.Text?.Trim();
            var index = _source.SelectedIndex;
            var page = await _client.GetItemsAsync(string.IsNullOrEmpty(search) && index > 0 ? _libraries[index - 1].Id : null, string.IsNullOrEmpty(search) ? null : search, _listed.Count, PageSize, _cts.Token);
            if (generation != _generation) return;
            AddTiles(page.Items);
        }
        catch (Exception ex) { if (generation == _generation) Fail(ex); }
        finally { _more.IsEnabled = true; }
    }

    private void AddTiles(IReadOnlyList<JellyfinItem> items)
    {
        foreach (var item in items)
        {
            _listed.Add(item);
            _tiles.Children.Add(Tile(item));
        }
        _more.IsVisible = _listed.Count < _total && items.Count > 0;
    }

    private Control Tile(JellyfinItem item)
    {
        var poster = new Border { Width = PosterWidth, Height = PosterHeight, CornerRadius = new CornerRadius(6), ClipToBounds = true };
        poster[!Border.BackgroundProperty] = new DynamicResourceExtension("Hover");
        var fallback = new TextBlock { Text = item.Name.Length > 0 ? item.Name[..1].ToUpperInvariant() : "?", FontSize = 36, Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        poster.Child = fallback;
        _ = LoadPosterAsync(item, poster);

        var title = new TextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = PosterWidth };
        var subtitle = new TextBlock { Text = item.Subtitle, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = PosterWidth, FontSize = 12 };
        subtitle.Classes.Add("muted");
        var stack = new StackPanel { Spacing = 2, Children = { poster, title, subtitle } };
        var button = new Button { Content = stack, Classes = { "ghost" }, Padding = new Thickness(6), Margin = new Thickness(0, 0, 8, 8), HorizontalContentAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(button, item.Name + (item.Subtitle.Length > 0 ? ", " + item.Subtitle : ""));
        button.Click += async (_, _) =>
        {
            if (item.Kind == JellyfinKind.Series) await OpenSeriesAsync(item);
            else if (item.Kind == JellyfinKind.Episode && item.SeriesId is { } series) { ShowDetail(item); await OpenSeriesAsync(new JellyfinItem(series, item.SeriesName, JellyfinKind.Series, null, 0, "", null, "", null, null, null, 0), item.Id, keepDetail: true); }
            else ShowDetail(item);
        };
        return button;
    }

    private async Task LoadPosterAsync(JellyfinItem item, Border into)
    {
        try
        {
            if (!_posters.TryGetValue(item.Id, out var bitmap))
            {
                var bytes = await _client.GetPosterAsync(item.Id, item.ImageTag, PosterWidth * 2, _cts.Token);
                if (bytes == null || _closing) return;
                using var stream = new MemoryStream(bytes);
                bitmap = Bitmap.DecodeToWidth(stream, PosterWidth * 2);
                _posters[item.Id] = bitmap;
            }
            into.Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }   // the letter stays
    }

    // ---- a show ----

    private void ShowGrid()
    {
        _seriesView.IsVisible = false;
        _gridView.IsVisible = true;
        _seriesId = null;
    }

    private async Task OpenSeriesAsync(JellyfinItem series, string? openEpisode = null, bool keepDetail = false)
    {
        _seriesId = series.Id;
        _gridView.IsVisible = false;
        _seriesView.IsVisible = true;
        _seriesTitle.Text = series.Name;
        _episodes.ItemsSource = null;
        _seasons.ItemsSource = null;
        if (!keepDetail) { _detail.IsVisible = false; ShowDetail(series); }
        Say("Loading…");
        try
        {
            var page = await _client.GetSeasonsAsync(series.Id, _cts.Token);
            if (_seriesId != series.Id) return;
            _seasons.ItemsSource = page.Items.Select(s => new SeasonChoice(s)).ToList();
            _pendingEpisode = openEpisode;
            _seasons.SelectedIndex = page.Items.Count > 0 ? 0 : -1;
            Say(page.Items.Count == 0 ? "No seasons were found." : "");
        }
        catch (Exception ex) { Fail(ex); }
    }

    private string? _pendingEpisode;

    private sealed record SeasonChoice(JellyfinItem Season)
    {
        public override string ToString() => Season.Name;
    }

    private async Task LoadEpisodesAsync()
    {
        if (_seasons.SelectedItem is not SeasonChoice choice || _seriesId is not { } series) return;
        Say("Loading…");
        try
        {
            var page = await _client.GetEpisodesAsync(series, choice.Season.Id, _cts.Token);
            if (_seriesId != series) return;
            var rows = page.Items.Select(e => new EpisodeRow(e)).ToList();
            _episodes.ItemsSource = rows;
            if (_pendingEpisode is { } want && rows.FirstOrDefault(r => r.Item.Id == want) is { } found) _episodes.SelectedItem = found;
            _pendingEpisode = null;
            Say(rows.Count == 0 ? "No episodes were found." : "");
        }
        catch (Exception ex) { Fail(ex); }
    }

    // ---- what the server knows about the one picked ----

    private void ShowDetail(JellyfinItem item)
    {
        _shown = item;
        _playButton.IsVisible = _play != null && item.Kind is JellyfinKind.Movie or JellyfinKind.Episode;
        _detailTitle.Text = item.Kind == JellyfinKind.Episode ? item.Name + (item.SeriesName.Length > 0 ? " · " + item.SeriesName : "") : item.Name;
        var line = new List<string>();
        if (item.Kind == JellyfinKind.Episode && item.SeasonNumber is { } s && item.Number is { } n) line.Add($"Season {s}, episode {n}");
        else if (item.Subtitle.Length > 0) line.Add(item.Subtitle);
        if (item.RuntimeMinutes > 0) line.Add(item.RuntimeMinutes >= 60 ? $"{item.RuntimeMinutes / 60} h {item.RuntimeMinutes % 60:00} min" : $"{item.RuntimeMinutes} min");
        if (item.PlayedPercent > 0 && item.PlayedPercent < 95) line.Add($"{(int)item.PlayedPercent}% watched");
        _detailLine.Text = string.Join("  ·  ", line);
        _detailText.Text = item.Overview;
        _detailText.IsVisible = item.Overview.Length > 0;
        _detail.IsVisible = true;
    }
}
