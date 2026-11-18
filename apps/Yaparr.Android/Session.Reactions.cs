using Avalonia.Threading;
using Yaparr.Chat;

namespace Yaparr.AndroidApp;

// Emoji reactions on the messages of a chat. The reactions are messages that are not shown as such (see ReactionEvent in the library); this counts them
// and puts the result on the lines, and sends yours.
public sealed partial class Session
{
    private readonly HashSet<Chat> _reactionsPending = new();

    // Reactions come in bursts (everyone tapping the same emoji): they are put on the messages once for the lot.
    private void ScheduleReactions(Chat chat)
    {
        if (!_reactionsPending.Add(chat)) return;
        DispatcherTimer.RunOnce(() =>
        {
            _reactionsPending.Remove(chat);
            RefreshReactions(chat);
        }, TimeSpan.FromMilliseconds(100));
    }

    private void RefreshReactions(Chat chat)
    {
        if (_node == null) return;
        ReactionSet? set = chat.Room?.GetReactions() ?? chat.Direct?.GetReactions();
        if (set == null) return;
        foreach (var line in chat.AllLines)
        {
            var reactions = set.For(line.Id);
            line.SetReactions(reactions, _node.AccountId);
            // The picture of a custom emoji used as a reaction that is not on this phone is asked for once (a live reaction fetches it by itself; this is for the ones
            // read back from the history).
            foreach (var custom in reactions.Select(r => r.Custom).OfType<CustomEmoji>())
                if (line.Source != null && !_node.HasAttachment(custom.Hash) && _reactionPicturesAsked.Add(custom.Hash))
                    _ = _node.FetchEmojiAsync(line.Source, custom);
        }
    }

    private readonly HashSet<string> _reactionPicturesAsked = new();

    /// <summary>The custom emoji of a name, yours first and then the room's, ready to be a reaction; null when this phone has no picture for the name.</summary>
    public CustomEmoji? PrepareReactionEmoji(Chat chat, string name)
    {
        if (_node == null) return null;
        var bytes = OwnEmoji.Read(name);
        if (bytes == null && chat.Room is { } room && room.Emoji.FirstOrDefault(e => e.Name == name) is { Available: true } info) bytes = room.GetGif(info.Id);
        if (bytes == null) return null;
        try { return _node.PrepareEmoji(name, bytes); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Puts a custom emoji on a message, or takes it off again.</summary>
    public async Task ReactAsync(Chat chat, Line line, CustomEmoji custom, bool on)
    {
        if (chat.Room is { } room) await room.SendReactionAsync(line.Id, custom, on, line.Channel);
        else if (chat.Direct is { } direct) await direct.SendReactionAsync(line.Id, custom, on);
        RefreshReactions(chat);
    }

    /// <summary>Puts an emoji on a message, or takes it off again.</summary>
    public async Task ReactAsync(Chat chat, Line line, string emoji, bool on)
    {
        if (chat.Room is { } room) await room.SendReactionAsync(line.Id, emoji, on, line.Channel);
        else if (chat.Direct is { } direct) await direct.SendReactionAsync(line.Id, emoji, on);
        RefreshReactions(chat);
    }
}
