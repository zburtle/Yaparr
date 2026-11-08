using Avalonia.Threading;

namespace Yaparr.AndroidApp;

/// <summary>The chat a notification was tapped for, waiting for the screen to open it. Only the newest matters.</summary>
internal static class ChatInbox
{
    /// <summary>The extra of the intent behind a notification, which names the chat (see <see cref="Session.KeyOf"/>).</summary>
    public const string ExtraChat = "org.yaparr.android.CHAT";

    private static string? s_waiting;

    /// <summary>A tap has arrived, on the UI thread.</summary>
    public static event Action? Arrived;

    public static void Offer(string? chatKey)
    {
        if (string.IsNullOrEmpty(chatKey) || chatKey.Length > 200) return;
        lock (typeof(ChatInbox)) s_waiting = chatKey;
        Dispatcher.UIThread.Post(() => Arrived?.Invoke());
    }

    public static string? Take()
    {
        lock (typeof(ChatInbox)) { var key = s_waiting; s_waiting = null; return key; }
    }
}
