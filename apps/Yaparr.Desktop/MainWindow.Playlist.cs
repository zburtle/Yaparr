using Avalonia.Controls;
using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Identity;
using Yaparr.Screen;

namespace Yaparr.Desktop;

// The room's watch playlist (see ChatNode.Playlist): what is queued to play after the film in the room. The person who streams plays it in order, as each film ends, and answers
// the questions of others who browse their library to add something; everyone who watches the film has the Playlist button in its view.
public partial class MainWindow
{
    // ---- changing what is playing ----

    // The Change… button of the player: the person's own library opens, and what is picked plays now, in place of what is playing.
    private Task ChangeFilmAsync()
    {
        if (_film == null || !_jellyfin.SignedIn || !Uri.TryCreate(_jellyfin.Url, UriKind.Absolute, out var server)) return Task.CompletedTask;
        JellyfinWindow? window = null;
        window = new JellyfinWindow(new JellyfinClient(server, JellyfinDeviceId(), _jellyfin.Token, _jellyfin.UserId), _jellyfin.ServerName,
            signedOut: ForgetJellyfinSignIn,
            play: item =>
            {
                _changeWindow?.Close();
                var title = item.Kind == JellyfinKind.Episode && item.SeriesName.Length > 0 ? $"{item.SeriesName}: {item.Name}" : item.Name;
                return SwitchFilmAsync(item.Id, title, item.RuntimeTicks, item.Kind == JellyfinKind.Episode ? item.SeriesId : null, null);
            },
            playLabel: "Play now");
        _changeWindow = window;
        window.Closed += (_, _) => { if (_changeWindow == window) _changeWindow = null; };
        window.Show(this);
        return Task.CompletedTask;
    }

    private JellyfinWindow? _changeWindow;

    // Starts something else on the stream that is running: the same share, so the room goes on watching it, with the sound and subtitles most like what was playing. What was
    // playing stays where it is (in the playlist, if it came from there); what starts from the playlist leaves it when it is done, like any other.
    private async Task SwitchFilmAsync(string itemId, string title, long fallbackTicks, string? seriesId, string? entryId)
    {
        if (_film is not { } sharer || _filmClient is not { } client) return;
        try
        {
            var info = await client.GetPlaybackInfoAsync(itemId);
            if (_film != sharer) return;
            _filmPlans[itemId] = new FilmPlan(info.SourceId,
                JellyfinClient.TrackLike(info.Audio, _filmAudioLike)?.Index,
                JellyfinClient.TrackLike(info.Subtitles, _filmSubtitleLike)?.Index);
            if (entryId != null) _filmEntries[itemId] = entryId;
            _filmSeriesId = seriesId;
            // Jellyfin is told where the one that is left stopped, not that it was watched to its end.
            if (_filmNow is { } now) _ = client.ReportPlaybackAsync("stop", now.ItemId, now.SourceId, now.Session, sharer.Position.Ticks);
            _filmSwitching = true;
            sharer.PlayNow(new FilmTarget(itemId, title, TimeSpan.FromTicks(info.RunTimeTicks > 0 ? info.RunTimeTicks : fallbackTicks)), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is JellyfinException or HttpRequestException or TaskCanceledException)
        {
            _log.Write("could not change the film: " + ex.Message);
            StatusText.Text = ex is JellyfinException { Unauthorized: true } ? "Jellyfin no longer accepts the sign-in. Sign in again in the settings." : "Could not play that: " + ex.Message;
        }
    }

    // The first item that can be played from the library this device streams from (an item from another streamer's library has to wait for that library).
    private PlaylistItem? PlaylistNextFor(ChatRoom room) => room.Playlist.Items.FirstOrDefault(i => i.ServerId == _filmServerId);

    // What was playing is done with, so it leaves the playlist if it came from there.
    private void FinishPlaylistEntry(string jellyfinItemId)
    {
        if (!_filmEntries.TryRemove(jellyfinItemId, out var entry) || _filmRoom is not { } room) return;
        try { room.RemoveFromPlaylist(entry); }
        catch (InvalidOperationException) { }
    }

    // The next thing queued in the playlist, made ready to play with the sound and subtitles most like what was playing; null when nothing is queued.
    private async Task<FilmTarget?> NextFromPlaylistAsync(ChatRoom room, JellyfinClient client, CancellationToken ct)
    {
        for (int tries = 0; tries < PlaylistLimits.MaxItems; tries++)
        {
            if (PlaylistNextFor(room) is not { } item) return null;
            try
            {
                var info = await client.GetPlaybackInfoAsync(item.ItemId, ct);
                _filmPlans[item.ItemId] = new FilmPlan(info.SourceId,
                    JellyfinClient.TrackLike(info.Audio, _filmAudioLike)?.Index,
                    JellyfinClient.TrackLike(info.Subtitles, _filmSubtitleLike)?.Index);
                _filmEntries[item.ItemId] = item.Id;
                _filmSeriesId = item.IsEpisode ? item.SeriesId : null;
                return new FilmTarget(item.ItemId, item.Title, TimeSpan.FromTicks(info.RunTimeTicks > 0 ? info.RunTimeTicks : item.RuntimeTicks));
            }
            catch (JellyfinException ex) when (!ex.Unauthorized)
            {
                // It is not on the server any more: it is taken off the playlist, and the one after it is tried.
                _log.Write($"“{item.Title}” in the playlist could not be played and was taken off: {ex.Message}");
                try { room.RemoveFromPlaylist(item.Id); }
                catch (InvalidOperationException) { return null; }
            }
        }
        return null;
    }

    // Someone who may add looks through this library: their question is answered from the server this device is signed in to.
    private async Task AnswerLibraryQuestionAsync(JellyfinClient client, PlaylistBrowseRequest request)
    {
        string answer;
        try { answer = await JellyfinBrowseServer.AnswerAsync(client, request.Body); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { answer = "{\"error\":\"" + "The streamer's library could not be read." + "\"}"; }
        request.Respond(answer);
    }

    // A film's view is open: you are in the room's stream, and its playlist shows under the people.
    private void EnablePlaylistUi(ScreenViewControl control, ChatRoom room, bool isHost, PeerId host)
    {
        if (_node == null) return;
        // You are in the stream while its view is open (watching it, or playing it): that is when the playlist shows under the people.
        _filmViewRooms[control] = room;
        RefreshPlaylistPlacement();
        control.Stopped += () =>
        {
            UI(() =>
            {
                _filmViewRooms.Remove(control);
                RefreshPlaylistPlacement();
            });
        };
    }

    // Where the playlist is shown: under the people while the film's view is in the main window, and to the right of the picture while it is in a window of its own (not in full
    // screen, where the window hides it). Looked at again whenever a view is opened, moved or closed.
    private void RefreshPlaylistPlacement()
    {
        foreach (var (control, room) in _filmViewRooms.ToList())
        {
            var watch = _screenViews.Values.FirstOrDefault(w => w.Control == control);
            if (watch?.Window is { } window)
            {
                if (watch.SidePlaylist == null)
                {
                    watch.SidePlaylist = NewPlaylistPanel(room, embedded: true);
                    window.SetSide(watch.SidePlaylist);
                }
            }
            else if (watch?.SidePlaylist is { } panel)
            {
                panel.Release();
                watch.SidePlaylist = null;
            }
        }
        UpdateSidebarPlaylist(SelectedRoom);
    }

    private readonly Dictionary<ScreenViewControl, ChatRoom> _filmViewRooms = new();

    private PlaylistPanel NewPlaylistPanel(ChatRoom room, bool embedded) => new(room, _node!.AccountId.Value,
        filmOn: () => room.MediaGroups.Any(g => g.Kind == FilmStatus.Kind),
        add: (isHost, host) => AddToPlaylistAsync(room, isHost, host),
        confirmClear: () => Dialogs.ConfirmAsync(this, "Clear the playlist", "Take everything off the playlist?", "Clear all", "Keep"),
        playNow: item => _ = SwitchFilmAsync(item.ItemId, item.Title, item.RuntimeTicks, item.IsEpisode ? item.SeriesId : null, item.Id),
        say: text => StatusText.Text = text,
        embedded);

    // ---- under the people of the room that is open ----

    private PlaylistPanel? _sidebarPlaylist;
    private ChatRoom? _sidebarRoom;
    private Action? _sidebarChanged;

    // While you are in a room's stream (watching its film, or playing one into it) its playlist shows under the people, about halfway down, even when it is empty (that is where
    // it is added to); otherwise nothing is there. Called whenever the people are listed again and when a film's view opens or closes.
    private void UpdateSidebarPlaylist(ChatRoom? room)
    {
        if (room != _sidebarRoom)
        {
            if (_sidebarRoom != null && _sidebarChanged != null) _sidebarRoom.PlaylistChanged -= _sidebarChanged;
            _sidebarPlaylist?.Release();
            _sidebarPlaylist = null;
            SidebarPlaylist.Child = null;
            _sidebarRoom = room;
            _sidebarChanged = null;
            if (room != null && _node != null)
            {
                _sidebarChanged = () => UI(() => UpdateSidebarPlaylist(room));
                room.PlaylistChanged += _sidebarChanged;
                _sidebarPlaylist = NewPlaylistPanel(room, embedded: true);
                SidebarPlaylist.Child = _sidebarPlaylist;
            }
        }
        bool show = room != null && _filmViewRooms.Any(kv => kv.Value == room && _screenViews.Values.FirstOrDefault(w => w.Control == kv.Key) is { Window: null });
        SidebarPlaylist.IsVisible = show;
        // The column is as wide as the room list while it holds the playlist, and as narrow as it was without.
        PeoplePanel.Width = show ? 346 : 236;
        PeopleGrid.RowDefinitions[1].Height = show ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    // The Jellyfin window opens on the library to pick from: the streamer's own, or for anyone else the streamer's, looked at through the streamer's computer.
    private Task AddToPlaylistAsync(ChatRoom room, bool isHost, PeerId host)
    {
        IJellyfinLibrary library;
        string name;
        if (isHost)
        {
            if (!_jellyfin.SignedIn || !Uri.TryCreate(_jellyfin.Url, UriKind.Absolute, out var server)) return Task.CompletedTask;
            library = new JellyfinClient(server, JellyfinDeviceId(), _jellyfin.Token, _jellyfin.UserId);
            name = _jellyfin.ServerName;
        }
        else
        {
            library = new RemoteJellyfinLibrary((question, ct) => room.AskLibraryAsync(host, question, ct));
            var streamer = room.Playlist.HostName;
            name = streamer.Length > 0 ? streamer + "'s library" : "the streamer's library";
        }
        JellyfinWindow? window = null;
        Task Add(JellyfinItem item)
        {
            var draft = new PlaylistDraft(item.Id,
                item.Kind == JellyfinKind.Episode && item.SeriesName.Length > 0 ? $"{item.SeriesName}: {item.Name}" : item.Name,
                item.Subtitle, item.Kind == JellyfinKind.Episode ? "episode" : "movie", item.RuntimeTicks, item.Kind == JellyfinKind.Episode ? item.SeriesId : null);
            try
            {
                if (isHost) room.AddToPlaylist(draft); else room.AskToAdd(host, draft);
                window?.Note(isHost ? $"Added “{draft.Title}” to the playlist." : $"Asked to add “{draft.Title}”. It shows in the playlist when the streamer's computer has added it.");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { window?.Note(ex.Message); }
            return Task.CompletedTask;
        }
        window = new JellyfinWindow(library, name, signedOut: () => { if (isHost) ForgetJellyfinSignIn(); }, Add, "Add to playlist");
        window.Show(this);
        return Task.CompletedTask;
    }
}
