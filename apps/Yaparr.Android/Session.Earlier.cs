using Yaparr.Chat;

namespace Yaparr.AndroidApp;

// Going back through a chat's history, by itself: when the person scrolls up to the oldest message on show, the next page is read from the history this phone keeps, and
// when there are no more of them here the others (the members of a room; your other devices and the person's in a direct chat) are asked for the next page, which is read
// when it arrives. While that goes on a spinner is the first row of the list; when there is nothing more the first row says "End Of Messages".
public sealed partial class Session
{
    private const int EarlierPage = 100;

    /// <summary>Older messages were put at the top of a chat: the message that was the oldest before is the one to keep on screen.</summary>
    public event Action<Chat, Line>? EarlierLoaded;

    /// <summary>The top of the list is in view: reads the next page of older messages (of the channel on show, in a room), or asks the others for it when this phone has no more. Does nothing while that is under way or when there is no more.</summary>
    public void LoadEarlier(Chat chat)
    {
        if (chat.Earlier != Chat.EarlierState.Idle) return;
        LoadEarlierCore(chat);
    }

    private void LoadEarlierCore(Chat chat)
    {
        if (_node == null || chat.OldestShown is not { } anchor || anchor.Source is not { } at) return;
        chat.SetEarlier(Chat.EarlierState.Loading);
        HistoryPage page = chat.Room is { } room ? room.GetEarlierHistory(chat.CurrentChannel, at.Key, EarlierPage)
            : chat.Direct is { } direct ? direct.GetEarlierHistory(at.Id, EarlierPage)
            : new HistoryPage(Array.Empty<ChatMessage>(), false);

        int before = chat.ShownMessages;
        chat.AddingEarlier = true;
        try
        {
            foreach (var m in page.Messages)
            {
                if (chat.Room is { } r) AddHistoryLine(chat, m, r.RoomId, counted: false);
                else
                {
                    var line = ToLine(m);
                    if (chat.Add(line)) TrackPicture(line);
                }
            }
        }
        finally { chat.AddingEarlier = false; }

        if (chat.ShownMessages > before)
        {
            chat.WaitingForEarlier = false;
            chat.SetEarlier(Chat.EarlierState.Idle);
            RefreshReactions(chat);
            EarlierLoaded?.Invoke(chat, anchor);
            return;
        }

        // The room's start record is held: this is the beginning for sure, so nobody is asked.
        if (page.AtStart) { chat.WaitingForEarlier = false; chat.SetEarlier(Chat.EarlierState.End); return; }

        // Nothing older here: the others are asked, and what they send is read when it arrives (or given up on after eight seconds, which is the end: what comes later is still read).
        int asked = chat.Room is { } asking ? asking.RequestEarlierHistory(chat.CurrentChannel, at.Key)
            : chat.Direct?.RequestEarlierHistory(at.Id) ?? 0;
        if (asked > 0)
        {
            chat.WaitingForEarlier = true;   // the spinner stays
            _ = GiveUpWaitingAsync(chat);
        }
        else chat.SetEarlier(Chat.EarlierState.End);
    }

    private async Task GiveUpWaitingAsync(Chat chat)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        UI(() =>
        {
            if (!chat.WaitingForEarlier) return;
            chat.WaitingForEarlier = false;
            chat.SetEarlier(Chat.EarlierState.End);
        });
    }

    // What was asked for has come: it is read, and the list shows it.
    private void OnEarlierArrived(Chat chat)
    {
        if (!chat.WaitingForEarlier) return;
        chat.WaitingForEarlier = false;
        chat.SetEarlier(Chat.EarlierState.Idle);
        LoadEarlierCore(chat);
    }
}
