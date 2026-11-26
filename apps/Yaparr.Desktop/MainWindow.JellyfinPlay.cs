using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Screen;

namespace Yaparr.Desktop;

// Playing a film or an episode from the signed-in Jellyfin server into the room that is open, as a share (see JellyfinSharer). The room has to be one where one of your devices is in
// the voice channel, as for sharing a screen; the picture and the sound go out to everyone in it. A panel over the chat has Pause, Stop, a position slider with jumps of 30 seconds,
// and for an episode the next one (which starts by itself when one ends, unless that is turned off).
public partial class MainWindow
{
    // What the server needs to play an item: its file, and which sound and subtitles. The first film has what was picked; the next episodes have what is most like it.
    private sealed record FilmPlan(string SourceId, int? Audio, int? Subtitle);
    private sealed record FilmNow(string ItemId, string SourceId, string Session);

    private JellyfinSharer? _film;
    private JellyfinClient? _filmClient;
    private ConversationVm? _filmConv;
    private Border? _filmPanel;
    private TextBlock? _filmText, _filmNote, _filmElapsed, _filmLength;
    private Button? _filmPause, _filmNext;
    private Slider? _filmSlider;
    private DispatcherTimer? _filmTimer;
    private readonly ConcurrentDictionary<string, FilmPlan> _filmPlans = new();
    private volatile FilmNow? _filmNow;
    private JellyfinPlaybackInfo? _filmInfo;
    private JellyfinTrack? _filmAudioLike, _filmSubtitleLike;
    private string? _filmSeriesId;
    private string? _filmNextTitle;
    private volatile bool _filmSubtitlesDropped;
    private bool _filmDragging;
    private string _filmTitle = "";
    private int _filmTicks;

    // The film picked in the Share window (see ShareDialog), played into the room that is open. The checks that it can be done (a room, voice, FFmpeg's libraries) were made before
    // the window was shown.
    private async Task StartFilmAsync(ConversationVm conv, ChatRoom room, FilmPlay film)
    {
        if (_node == null || !_jellyfin.SignedIn || !Uri.TryCreate(_jellyfin.Url, UriKind.Absolute, out var server)) return;
        if (_film != null || _sharer != null) return;   // something started while the window was open
        var item = film.Item;
        var info = film.Info;
        var choice = film.Choice;
        var client = new JellyfinClient(server, JellyfinDeviceId(), _jellyfin.Token, _jellyfin.UserId);
        try
        {
            _filmPlans.Clear();
            _filmPlans[item.Id] = new FilmPlan(info.SourceId, choice.AudioIndex, choice.SubtitleIndex);
            _filmAudioLike = info.Audio.FirstOrDefault(t => t.Index == choice.AudioIndex);
            _filmSubtitleLike = info.Subtitles.FirstOrDefault(t => t.Index == choice.SubtitleIndex);
            _filmSeriesId = item.Kind == JellyfinKind.Episode ? item.SeriesId : null;
            _filmInfo = info;

            var first = new FilmTarget(item.Id, film.Title, TimeSpan.FromTicks(info.RunTimeTicks > 0 ? info.RunTimeTicks : item.RuntimeTicks));
            FilmOpener open = async (target, start, ct) =>
            {
                if (!_filmPlans.TryGetValue(target.Id, out var plan)) throw new InvalidOperationException("Nothing is known about that film.");
                // Subtitles in the picture and a start part-way in do not go together (the sound would not belong to the picture), so after a jump they are off.
                int? subtitle = start > TimeSpan.Zero ? null : plan.Subtitle;
                _filmSubtitlesDropped = plan.Subtitle != null && subtitle == null;
                var request = new JellyfinStreamRequest(target.Id, plan.SourceId, Guid.NewGuid().ToString("N"), choice.Kbps, choice.Height, plan.Audio, subtitle, start.Ticks);
                var stream = await client.OpenStreamAsync(request, ct);
                var before = _filmNow;
                _filmNow = new FilmNow(target.Id, plan.SourceId, request.PlaySessionId);
                if (before == null) _ = client.ReportPlaybackAsync("start", target.Id, plan.SourceId, request.PlaySessionId, start.Ticks);
                return (stream.Body, (IAsyncDisposable)stream);
            };
            FilmNext next = async (current, ct) =>
            {
                if (_filmSeriesId == null) return null;
                var after = (await client.GetEpisodesAfterAsync(_filmSeriesId, current.Id, 1, ct)).FirstOrDefault();
                if (after == null) return null;
                var nextInfo = await client.GetPlaybackInfoAsync(after.Id, ct);
                _filmPlans[after.Id] = new FilmPlan(nextInfo.SourceId,
                    JellyfinClient.TrackLike(nextInfo.Audio, _filmAudioLike)?.Index,
                    JellyfinClient.TrackLike(nextInfo.Subtitles, _filmSubtitleLike)?.Index);
                return new FilmTarget(after.Id, $"{(after.SeriesName.Length > 0 ? after.SeriesName : item.SeriesName)}: {after.Name}", TimeSpan.FromTicks(nextInfo.RunTimeTicks > 0 ? nextInfo.RunTimeTicks : after.RuntimeTicks));
            };

            var sharer = JellyfinSharer.Start(_node, room, film.Title, choice.Kbps, first, TimeSpan.FromTicks(choice.StartTicks), open, next);
            _film = sharer;
            _filmClient = client;
            _filmConv = conv;
            _filmTitle = film.Title;
            _filmTicks = 0;
            _log.Write($"playing “{film.Title}” from Jellyfin in the room {conv.Name}: {choice.Height}p, {choice.Kbps} kbit/s, sound #{choice.AudioIndex?.ToString() ?? "default"}, subtitles {(choice.SubtitleIndex is { } s ? "#" + s : "off")}, from {TimeSpan.FromTicks(choice.StartTicks):h\\:mm\\:ss}");
            sharer.Ended += ex => UI(() =>
            {
                if (_film != sharer) return;
                _log.Write(ex == null ? "the film ended" : $"the film stopped: {ex.Message} ({ex.GetType().Name})");
                var text = ex == null ? $"“{_filmTitle}” has ended." : "The film stopped: " + (ex is JellyfinException { Unauthorized: true } ? "Jellyfin no longer accepts the sign-in." : ex.Message);
                StopFilm();
                conv.Add(MessageVm.System(text));
                if (ex is JellyfinException { Unauthorized: true }) ForgetJellyfinSignIn();
            });
            FilmTarget previous = first;
            sharer.TargetChanged += target => UI(() =>
            {
                if (_film != sharer) return;
                // Jellyfin is told that the last one was watched to its end and the next has begun.
                if (_filmNow is { } now && _filmClient is { } c)
                {
                    _ = c.ReportPlaybackAsync("stop", previous.Id, _filmPlans.TryGetValue(previous.Id, out var old) ? old.SourceId : now.SourceId, now.Session, previous.Duration.Ticks);
                    if (_filmPlans.TryGetValue(target.Id, out var plan)) _ = c.ReportPlaybackAsync("start", target.Id, plan.SourceId, now.Session, 0);
                }
                previous = target;
                _filmTitle = target.Title;
                _log.Write($"the film went on to “{target.Title}”");
                conv.Add(MessageVm.System($"Now playing “{target.Title}”."));
                if (_filmSlider != null) _filmSlider.Maximum = Math.Max(1, target.Duration.TotalSeconds);
                _ = RefreshFilmNextAsync(sharer);
            });
            conv.IsSharing = true;
            // The film is seen (and heard) by you too, as soon as it starts, with its player under the picture.
            WatchOwnFilm(new ShareVm(sharer.Source.Info.Id, film.Title, isMine: true, sharer.Source.Info.Root.Value), conv);
            BuildFilmPanel(sharer, conv, room);
            _ = RefreshFilmNextAsync(sharer);
            conv.Add(MessageVm.System(conv.InVoice
                ? $"You are playing “{film.Title}”. Everyone in the room can watch it."
                : $"You are playing “{film.Title}” through the call on your other device. Everyone in the room can watch it."));
        }
        catch (Exception ex)
        {
            client.Dispose();
            _log.Write("could not play the film: " + ex.Message);
            await Dialogs.MessageAsync(this, "Play in a room", ex is JellyfinException { Unauthorized: true } ? "Jellyfin no longer accepts the sign-in. Sign in again in the settings." : "Could not start the film: " + ex.Message);
            if (ex is JellyfinException { Unauthorized: true }) ForgetJellyfinSignIn();
        }
    }

    // Looks up what comes after what is playing (for the Next button and its tip).
    private async Task RefreshFilmNextAsync(JellyfinSharer sharer)
    {
        _filmNextTitle = null;
        if (_filmSeriesId is { } series && _filmClient is { } client)
        {
            try
            {
                var after = (await client.GetEpisodesAfterAsync(series, sharer.Target.Id, 1)).FirstOrDefault();
                if (after != null) _filmNextTitle = $"{(after.SeriesName.Length > 0 ? after.SeriesName : "")}{(after.SeriesName.Length > 0 ? ": " : "")}{after.Name}";
            }
            catch (Exception ex) when (ex is JellyfinException or HttpRequestException or TaskCanceledException or ObjectDisposedException) { }
        }
        UI(UpdateFilmPanel);
    }

    // The drawings of the player's buttons, in a 24 by 24 box and all filled shapes, so that they are one family.
    private const string PlayPath = "M8,5 L19,12 L8,19 Z";
    private const string PausePath = "M7,5 H11 V19 H7 Z M13,5 H17 V19 H13 Z";
    private const string NextPath = "M6,6 L15,12 L6,18 Z M16,6 H18.5 V18 H16 Z";
    private const string StopPath = "M7,7 H17 V17 H7 Z";
    private bool _filmShowsPlay;

    // A drawing in the colour of the button it is in.
    private static Control FilmGlyph(string data, int size)
    {
        var shape = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(data) };
        shape.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, new Avalonia.Data.Binding
        {
            Path = nameof(Button.Foreground),
            RelativeSource = new Avalonia.Data.RelativeSource(Avalonia.Data.RelativeSourceMode.FindAncestor) { AncestorType = typeof(Button) },
        });
        return new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 24, Height = 24, Children = { shape } } };
    }

    // Takes the border and the background off the buttons that have the class "flat", and dims them a little under the pointer or when pressed.
    private static Avalonia.Styling.Style FlatButtonStyle(string? state = null, double opacity = 1)
    {
        if (state == null)
            return new Avalonia.Styling.Style(x => x.OfType<Button>().Class("flat"))
            {
                Setters = { new Avalonia.Styling.Setter(Button.BackgroundProperty, Brushes.Transparent), new Avalonia.Styling.Setter(Button.BorderThicknessProperty, new Thickness(0)) },
            };
        return new Avalonia.Styling.Style(x => x.OfType<Button>().Class("flat").Class(state).Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters = { new Avalonia.Styling.Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, Brushes.Transparent), new Avalonia.Styling.Setter(Visual.OpacityProperty, opacity) },
        };
    }

    private void BuildFilmPanel(JellyfinSharer sharer, ConversationVm conv, ChatRoom room)
    {
        // Laid out as a video player is: the position with the times at its ends, and under it the buttons (back, play or pause, forward, next) with what is playing beside them.
        // In the view of the film, what is playing is the line at the bottom left and the sound controls are beside the buttons; without the view it is all in the panel.
        _screenViews.TryGetValue(sharer.Source.Info.Id, out var view);
        // Flat, as a video player's are: no border and no background, just the drawing (all of one family, see FilmGlyph), a little dimmer under the pointer.
        IBrush? ink = view != null ? Brushes.White : null;   // the bar under the picture is black, whatever the theme
        Button Icon(string data, string tip, int size = 24, int width = 56)
        {
            var button = new Button { Content = FilmGlyph(data, size), Classes = { "flat" }, Width = width, Height = 40, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            if (ink != null) button.Foreground = ink;
            ToolTip.SetTip(button, tip);
            AutomationProperties.SetName(button, tip);
            return button;
        }
        Button Seconds(string text, string tip)
        {
            var button = new Button { Content = text, Classes = { "flat" }, Width = 56, Height = 40, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center };
            if (ink != null) button.Foreground = ink;
            ToolTip.SetTip(button, tip);
            return button;
        }

        _filmText = view != null ? view.Control.CaptionBlock : new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _filmElapsed = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, MinWidth = 52, TextAlignment = TextAlignment.Right };
        _filmLength = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, MinWidth = 52 };
        _filmPause = Icon(PausePath, "Pause", 28);
        _filmShowsPlay = false;
        _filmPause.Click += (_, _) =>
        {
            if (sharer.Paused) sharer.Resume(); else sharer.Pause();
            UpdateFilmPanel();
        };

        // The position: drag or click to jump, or take 30 seconds back or forward. A jump starts the film again at that time on the server, which takes a moment.
        var back = Seconds("−30 s", "Back 30 seconds");
        back.Click += (_, _) => sharer.Seek(sharer.Position - TimeSpan.FromSeconds(30));
        var forward = Seconds("+30 s", "Forward 30 seconds");
        forward.Click += (_, _) => sharer.Seek(sharer.Position + TimeSpan.FromSeconds(30));
        _filmSlider = new Slider { Minimum = 0, Maximum = Math.Max(1, sharer.Target.Duration.TotalSeconds), IsEnabled = sharer.Target.Duration > TimeSpan.Zero, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        AutomationProperties.SetName(_filmSlider, "Position in the film");
        _filmSlider.AddHandler(PointerPressedEvent, (_, _) => _filmDragging = true, handledEventsToo: true);
        void Release()
        {
            if (!_filmDragging) return;
            _filmDragging = false;
            if (_film == sharer && _filmSlider != null) sharer.Seek(TimeSpan.FromSeconds(_filmSlider.Value));
        }
        _filmSlider.AddHandler(PointerReleasedEvent, (_, _) => Release(), handledEventsToo: true);
        _filmSlider.PointerCaptureLost += (_, _) => Release();
        var position = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(_filmSlider, 1);
        Grid.SetColumn(_filmLength, 2);
        position.Children.Add(_filmElapsed);
        position.Children.Add(_filmSlider);
        position.Children.Add(_filmLength);

        _filmNext = Icon(NextPath, "Next episode", 20);
        _filmNext.IsVisible = false;
        _filmNext.Click += (_, _) => sharer.Next();
        var auto = new CheckBox { Content = "Autoplay next", IsChecked = sharer.Autoplay, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, IsVisible = _filmSeriesId != null };
        auto.IsCheckedChanged += (_, _) => sharer.Autoplay = auto.IsChecked == true;
        ToolTip.SetTip(auto, "Play the next episode by itself when this one ends");
        // The buttons are in the middle under the position, the sound at the left of the same line, the autoplay box at the right.
        var transport = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Children = { back, _filmPause, forward, _filmNext } };
        var controls = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), Margin = new Thickness(0, 4, 0, 0) };
        Grid.SetColumn(transport, 1);
        Grid.SetColumn(auto, 2);
        auto.HorizontalAlignment = HorizontalAlignment.Right;
        controls.Children.Add(transport);
        controls.Children.Add(auto);
        if (view != null)
        {
            var sound = view.Control.TakeSoundPanel();
            sound.HorizontalAlignment = HorizontalAlignment.Stretch;
            sound.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(sound, 0);
            Grid.SetRow(sound, 0);
            controls.Children.Add(sound);
        }
        else
        {
            _filmText.Margin = new Thickness(0, 0, 10, 0);
            controls.Children.Add(_filmText);
        }
        _filmNote = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false, Text = "Subtitles are off after a jump: the server cannot put them into the picture from part-way in." };
        _filmNote.Classes.Add("muted");

        var panel = new StackPanel { Spacing = 0, Children = { position, controls, _filmNote } };
        panel.Styles.Add(FlatButtonStyle());
        panel.Styles.Add(FlatButtonStyle(":pointerover", 0.7));
        panel.Styles.Add(FlatButtonStyle(":pressed", 0.5));
        if (view != null)
        {
            _filmText.Foreground = Brushes.LightGray; _filmElapsed.Foreground = Brushes.White; _filmLength.Foreground = Brushes.White; _filmNote.Foreground = Brushes.LightGray; auto.Foreground = Brushes.White;
            view.Control.AttachPlayer(panel);   // under the picture, like a video player
        }
        else
        {
            // The view could not be shown: the player goes where the sharing panel is, so that the film can still be controlled and stopped.
            var stop = Icon(StopPath, "Stop Stream", 20);
            stop.Click += (_, _) =>
            {
                var title = _filmTitle;
                StopFilm();
                conv.Add(MessageVm.System($"You stopped “{title}”."));
            };
            transport.Children.Add(stop);
            panel.Margin = new Thickness(10, 8);
            _filmPanel = new Border { Classes = { "details" }, Margin = new Thickness(0, 0, 0, 6), Child = panel };
            SharerPanel.Child = _filmPanel;
            UpdateSharerPanel();
        }
        UpdateFilmPanel();

        // The film stops when none of your devices is in the voice channel any more, as a shared screen does.
        var channel = sharer.Source.Info.Channel ?? RoomChannels.DefaultVoice;
        int missed = 0;
        _filmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _filmTimer.Tick += (_, _) =>
        {
            if (_film != sharer) return;
            UpdateFilmPanel();
            if (++_filmTicks % 4 == 0)
            {
                missed = room.ScreenShareChannel == channel ? 0 : missed + 1;
                if (missed >= 3)
                {
                    _log.Write("the film stopped: none of your devices is in the voice channel any more");
                    StopFilm();
                    conv.Add(MessageVm.System("The film stopped: none of your devices is in the call any more."));
                    return;
                }
            }
            // Jellyfin is told where the film is every ten seconds, so that "continue watching" follows along.
            if (_filmTicks % 10 == 0 && _filmClient is { } client && _filmNow is { } now)
                _ = client.ReportPlaybackAsync("progress", now.ItemId, now.SourceId, now.Session, sharer.Position.Ticks);
        };
        _filmTimer.Start();
    }

    private void UpdateFilmPanel()
    {
        if (_film is not { } sharer || _filmText == null || _filmPause == null) return;
        var viewers = sharer.Source.Viewers.Count;
        var at = sharer.Position;
        var length = sharer.Target.Duration;
        static string Clock(TimeSpan t) => t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
        _filmText.Text = $"{sharer.Target.Title}   ·   {viewers} watching";
        if (_filmElapsed != null) _filmElapsed.Text = Clock(at);
        if (_filmLength != null) _filmLength.Text = length > TimeSpan.Zero ? Clock(length) : "";
        if (_filmShowsPlay != sharer.Paused)
        {
            _filmShowsPlay = sharer.Paused;
            _filmPause.Content = FilmGlyph(sharer.Paused ? PlayPath : PausePath, 28);
        }
        ToolTip.SetTip(_filmPause, sharer.Paused ? "Resume" : "Pause");
        if (_filmSlider != null && !_filmDragging && length > TimeSpan.Zero)
        {
            _filmSlider.Maximum = Math.Max(1, length.TotalSeconds);
            _filmSlider.Value = Math.Clamp(at.TotalSeconds, 0, _filmSlider.Maximum);
        }
        if (_filmNext != null)
        {
            _filmNext.IsVisible = _filmNextTitle != null;
            ToolTip.SetTip(_filmNext, _filmNextTitle == null ? null : "Next: " + _filmNextTitle);
        }
        if (_filmNote != null) _filmNote.IsVisible = _filmSubtitlesDropped;
    }

    private void StopFilm()
    {
        var sharer = _film;
        if (sharer == null) return;
        var viewId = sharer.Source.Info.Id;
        var client = _filmClient;
        var now = _filmNow;
        var position = sharer.Position.Ticks;
        _film = null;
        // The view of it that you have closes with it (it does not stop the film again: that is over).
        if (_screenViews.TryGetValue(viewId, out var own)) EndWatch(own);
        _filmClient = null;
        _filmNow = null;
        _filmPlans.Clear();
        _filmInfo = null;
        _filmSeriesId = null;
        _filmNextTitle = null;
        _filmSubtitlesDropped = false;
        _filmTimer?.Stop();
        _filmTimer = null;
        _filmPanel = null;
        _filmText = null;
        _filmElapsed = null;
        _filmLength = null;
        _filmNote = null;
        _filmNext = null;
        _filmSlider = null;
        _filmPause = null;
        if (_filmConv is { } conv) conv.IsSharing = false;
        _filmConv = null;
        SharerPanel.Child = null;
        SharerPanel.IsVisible = false;
        UpdateSharerPanel();
        // Stopping waits for the transcode to end on the server; that is not done on this thread.
        _ = Task.Run(async () =>
        {
            try
            {
                if (client != null && now != null) await client.ReportPlaybackAsync("stop", now.ItemId, now.SourceId, now.Session, position);
                sharer.Dispose();
            }
            catch (Exception ex) { UI(() => _log.Write("stopping the film: " + ex.Message)); }
            finally { client?.Dispose(); }
        });
    }
}
