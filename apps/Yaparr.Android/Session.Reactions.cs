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
        foreach (var line in chat.AllLines) line.SetReactions(set.For(line.Id), _node.AccountId);
    }

    /// <summary>Puts an emoji on a message, or takes it off again.</summary>
    public async Task ReactAsync(Chat chat, Line line, string emoji, bool on)
    {
        if (chat.Room is { } room) await room.SendReactionAsync(line.Id, emoji, on, line.Channel);
        else if (chat.Direct is { } direct) await direct.SendReactionAsync(line.Id, emoji, on);
        RefreshReactions(chat);
    }
}
