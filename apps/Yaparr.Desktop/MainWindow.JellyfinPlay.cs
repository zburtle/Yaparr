using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Screen;

namespace Yaparr.Desktop;

// Playing a film or an episode from the signed-in Jellyfin server into the room that is open, as a share (see JellyfinSharer). The room has to be one where one of your devices is in
// the voice channel, as for sharing a screen; the picture and the sound go out to everyone in it, and a small panel over the chat has Pause and Stop.
public partial class MainWindow
{
    private JellyfinSharer? _film;
    private JellyfinClient? _filmClient;
    private ConversationVm? _filmConv;
    private Border? _filmPanel;
    private TextBlock? _filmText;
    private Button? _filmPause;
    private DispatcherTimer? _filmTimer;
    private JellyfinStreamRequest? _filmRequest;
    private string _filmTitle = "";
    private int _filmTicks;

    private async Task PlayFilmAsync(JellyfinItem item)
    {
        if (_node == null || !_jellyfin.SignedIn || !Uri.TryCreate(_jellyfin.Url, UriKind.Absolute, out var server)) return;
        if (_film != null) { await Dialogs.MessageAsync(this, "Play in a room", $"“{_filmTitle}” is already playing. Stop it first."); return; }
        if (_sharer != null) { await Dialogs.MessageAsync(this, "Play in a room", "You are sharing your screen. Stop that first."); return; }
        if (ScreenGate.Instance.Blocked) { StatusText.Text = ScreenGate.Why; return; }
        if (_selected is not { IsRoom: true } conv || _roomConversations.FirstOrDefault(kv => kv.Value == conv).Key is not { } room)
        {
            await Dialogs.MessageAsync(this, "Play in a room", "Open the room to play in first (pick it in the list on the left), and join its voice channel.");
            return;
        }
        if (!room.CanShareScreen) { await Dialogs.MessageAsync(this, "Play in a room", $"Join the voice channel of “{conv.Name}” first, on one of your devices."); return; }
        if (!await GetFfmpegLibrariesAsync()) return;
        if (!AacDecoder.IsAvailable)
        {
            await Dialogs.MessageAsync(this, "Play in a room", "The FFmpeg libraries on this computer cannot read a film's sound. Update Yaparr, which brings newer ones.");
            return;
        }

        var client = new JellyfinClient(server, JellyfinDeviceId(), _jellyfin.Token, _jellyfin.UserId);
        try
        {
            var title = item.Kind == JellyfinKind.Episode && item.SeriesName.Length > 0 ? $"{item.SeriesName}: {item.Name}" : item.Name;
            var info = await client.GetPlaybackInfoAsync(item.Id);
            long resume = item.PlayedPercent is > 1 and < 95 && item.RuntimeTicks > 0 ? (long)(item.RuntimeTicks * item.PlayedPercent / 100) : 0;
            if (await FilmOptionsDialog.AskAsync(this, title, info, resume, _node.MeasuredUploadBytesPerSecond ?? 0, viewersHint: 2) is not { } choice) { client.Dispose(); return; }
            if (_film != null || _sharer != null) { client.Dispose(); return; }   // something started while the dialog was open

            var request = new JellyfinStreamRequest(item.Id, info.SourceId, Guid.NewGuid().ToString("N"), choice.Kbps, choice.Height, choice.AudioIndex, choice.SubtitleIndex, choice.StartTicks);
            var sharer = JellyfinSharer.Start(_node, room, title, choice.Kbps, async ct =>
            {
                var stream = await client.OpenStreamAsync(request, ct);
                return (stream.Body, (IAsyncDisposable)stream);
            });
            _film = sharer;
            _filmClient = client;
            _filmConv = conv;
            _filmRequest = request;
            _filmTitle = title;
            _filmTicks = 0;
            _log.Write($"playing “{title}” from Jellyfin in the room {conv.Name}: {choice.Height}p, {choice.Kbps} kbit/s, sound #{choice.AudioIndex?.ToString() ?? "default"}, subtitles {(choice.SubtitleIndex is { } s ? "#" + s : "off")}, from {TimeSpan.FromTicks(choice.StartTicks):h\\:mm\\:ss}");
            sharer.Ended += ex => UI(() =>
            {
                if (_film != sharer) return;
                _log.Write(ex == null ? "the film ended" : $"the film stopped: {ex.Message} ({ex.GetType().Name})");
                var text = ex == null ? $"“{title}” has ended." : "The film stopped: " + (ex is JellyfinException { Unauthorized: true } ? "Jellyfin no longer accepts the sign-in." : ex.Message);
                StopFilm();
                conv.Add(MessageVm.System(text));
                if (ex is JellyfinException { Unauthorized: true }) ForgetJellyfinSignIn();
            });
            _ = client.ReportPlaybackAsync("start", item.Id, info.SourceId, request.PlaySessionId, choice.StartTicks);
            BuildFilmPanel(sharer, conv, room);
            conv.IsSharing = true;
            conv.Add(MessageVm.System(conv.InVoice
                ? $"You are playing “{title}”. Everyone in the room can watch it."
                : $"You are playing “{title}” through the call on your other device. Everyone in the room can watch it."));
        }
        catch (Exception ex)
        {
            client.Dispose();
            _log.Write("could not play the film: " + ex.Message);
            await Dialogs.MessageAsync(this, "Play in a room", ex is JellyfinException { Unauthorized: true } ? "Jellyfin no longer accepts the sign-in. Sign in again in the settings." : "Could not start the film: " + ex.Message);
            if (ex is JellyfinException { Unauthorized: true }) ForgetJellyfinSignIn();
        }
    }

    private void BuildFilmPanel(JellyfinSharer sharer, ConversationVm conv, ChatRoom room)
    {
        _filmText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _filmPause = new Button { Content = "Pause", MinWidth = 70, HorizontalContentAlignment = HorizontalAlignment.Center };
        _filmPause.Click += (_, _) =>
        {
            if (sharer.Paused) sharer.Resume(); else sharer.Pause();
            UpdateFilmPanel();
        };
        var stop = new Button { Content = "Stop", MinWidth = 70, HorizontalContentAlignment = HorizontalAlignment.Center };
        stop.Click += (_, _) =>
        {
            StopFilm();
            conv.Add(MessageVm.System($"You stopped “{_filmTitle}”."));
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _filmPause, stop } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(10, 8) };
        grid.Children.Add(_filmText);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        _filmPanel = new Border { Classes = { "details" }, Margin = new Thickness(0, 0, 0, 6), Child = grid };
        SharerPanel.Child = _filmPanel;
        UpdateSharerPanel();
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
            if (_filmTicks % 10 == 0 && _filmClient is { } client && _filmRequest is { } r)
                _ = client.ReportPlaybackAsync("progress", r.ItemId, r.SourceId, r.PlaySessionId, r.StartTicks + sharer.Position.Ticks);
        };
        _filmTimer.Start();
    }

    private void UpdateFilmPanel()
    {
        if (_film is not { } sharer || _filmText == null || _filmPause == null) return;
        var viewers = sharer.Source.Viewers.Count;
        var at = TimeSpan.FromTicks((_filmRequest?.StartTicks ?? 0) + sharer.Position.Ticks);
        _filmText.Text = $"{(sharer.Paused ? "Paused" : "Playing")}: {_filmTitle}   ·   {at:h\\:mm\\:ss}   ·   {viewers} watching";
        _filmPause.Content = sharer.Paused ? "Resume" : "Pause";
    }

    private void StopFilm()
    {
        var sharer = _film;
        if (sharer == null) return;
        var client = _filmClient;
        var request = _filmRequest;
        var position = (request?.StartTicks ?? 0) + sharer.Position.Ticks;
        _film = null;
        _filmClient = null;
        _filmRequest = null;
        _filmTimer?.Stop();
        _filmTimer = null;
        _filmPanel = null;
        _filmText = null;
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
                if (client != null && request != null) await client.ReportPlaybackAsync("stop", request.ItemId, request.SourceId, request.PlaySessionId, position);
                sharer.Dispose();
            }
            catch (Exception ex) { UI(() => _log.Write("stopping the film: " + ex.Message)); }
            finally { client?.Dispose(); }
        });
    }
}
