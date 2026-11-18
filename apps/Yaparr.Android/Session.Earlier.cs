using Yaparr.Chat;

namespace Yaparr.AndroidApp;

// Going back through a chat's history. A chat opens with the newest messages of each channel; older ones are read a page at a time from the history this phone
// keeps, and when there are no more of them here the others (the members of a room; your other devices and the person's in a direct chat) are asked for the next
// page, which is read when it arrives.
public sealed partial class Session
{
    private const int EarlierPage = 100;

    /// <summary>Older messages were put at the top of a chat: the message that was the oldest before is the one to keep on screen.</summary>
    public event Action<Chat, Line>? EarlierLoaded;

    /// <summary>Reads the next page of older messages (of the channel on show, in a room), or asks the others for it when this phone has no more.</summary>
    public void LoadEarlier(Chat chat)
    {
        if (_node == null || chat.Shown.Count == 0) return;
        var anchor = chat.Shown[0];
        if (anchor.Source is not { } at) return;
        HistoryPage page = chat.Room is { } room ? room.GetEarlierHistory(chat.CurrentChannel, at.Key, EarlierPage)
            : chat.Direct is { } direct ? direct.GetEarlierHistory(at.Id, EarlierPage)
            : new HistoryPage(Array.Empty<ChatMessage>(), false);

        int before = chat.Shown.Count;
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

        if (chat.Shown.Count > before)
        {
            chat.WaitingForEarlier = false;
            chat.SetEarlier("Load earlier messages");
            RefreshReactions(chat);
            EarlierLoaded?.Invoke(chat, anchor);
            return;
        }

        // Nothing older here: the others are asked, and what they send is read when it arrives (or given up on after a while).
        int asked = chat.Room is { } asking ? asking.RequestEarlierHistory(chat.CurrentChannel, at.Key)
            : chat.Direct?.RequestEarlierHistory(at.Id) ?? 0;
        if (asked > 0)
        {
            chat.WaitingForEarlier = true;
            chat.SetEarlier("Asking the others for earlier messages…");
            _ = GiveUpWaitingAsync(chat);
        }
        else chat.SetEarlier("No earlier messages here, and nobody is connected who could have more");
    }

    private async Task GiveUpWaitingAsync(Chat chat)
    {
        await Task.Delay(TimeSpan.FromSeconds(20));
        UI(() =>
        {
            if (!chat.WaitingForEarlier) return;
            chat.WaitingForEarlier = false;
            chat.SetEarlier("The others have no earlier messages. Tap to try again.");
        });
    }

    // What was asked for has come: it is read, and the list shows it.
    private void OnEarlierArrived(Chat chat)
    {
        if (chat.WaitingForEarlier) LoadEarlier(chat);
    }
}
