namespace Yaparr.AppCore;

/// <summary>
/// When a new message is worth a notification on the phone, and what the notification says. Direct messages, mentions and replies to you always are; other
/// messages in a room only when that room is set to notify for every message. Nothing is shown while the app is open (the unread marks do that), for a
/// message that is old (history arriving after a reconnect), or for one you wrote yourself. The lock screen shows who and where, never the words.
/// </summary>
internal static class NotificationPolicy
{
    /// <summary>How old a message can be and still be news: anything older came with the history.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(2);

    /// <param name="Mine">Written by you, on this phone or another of your devices.</param>
    /// <param name="MentionsMe">Names you (or everyone, from an admin).</param>
    /// <param name="ReplyToMe">Answers one of your messages.</param>
    /// <param name="RoomNotifiesAll">The room is set to notify for every message.</param>
    public readonly record struct Message(bool IsDirect, bool Mine, bool MentionsMe, bool ReplyToMe, bool RoomNotifiesAll, TimeSpan Age);

    public static bool ShouldNotify(in Message message, bool appInForeground, bool notificationsOn)
    {
        if (!notificationsOn || appInForeground || message.Mine || message.Age > Fresh) return false;
        return message.IsDirect || message.MentionsMe || message.ReplyToMe || message.RoomNotifiesAll;
    }

    /// <summary>
    /// What shows on the lock screen: who, and in which room for a room message ("Alex in the new room"). The words are not there until the phone is
    /// unlocked.
    /// </summary>
    public static string LockScreenText(string sender, string chatTitle, bool isDirect) =>
        isDirect || string.Equals(sender, chatTitle, StringComparison.Ordinal) ? Clean(sender, 60) : Clean(sender, 40) + " in " + Clean(chatTitle, 40);

    /// <summary>What a message says in the notification once the phone is unlocked: its words, or what it carries.</summary>
    public static string Body(string text, bool hasPicture, bool isGif)
    {
        var words = Clean(text, 140);
        if (words.Length > 0) return hasPicture ? (isGif ? "GIF: " : "Picture: ") + words : words;
        return hasPicture ? (isGif ? "Sent a GIF" : "Sent a picture") : "";
    }

    // One line, without control characters, cut to length.
    private static string Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new System.Text.StringBuilder(Math.Min(text.Length, max + 1));
        bool space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
            if (sb.Length > max) break;
        }
        var clean = sb.ToString().Trim();
        return clean.Length <= max ? clean : clean[..(max - 1)].TrimEnd() + "…";
    }
}
