using Android.App;
using Android.Content;
using Avalonia.Threading;

namespace Yaparr.AndroidApp;

/// <summary>
/// What is typed into a message notification's Reply box. It is sent as a message to that chat, in the channel the notification was for, through the one session of
/// the process; if there is none that is connected (Android stopped the app, or it has not started) the notification says so and nothing is sent: a reply is never
/// held back to be sent later without the person knowing.
/// </summary>
[BroadcastReceiver(Exported = false)]
public sealed class NotificationReplyReceiver : BroadcastReceiver
{
    [System.Runtime.Versioning.SupportedOSPlatform("android26.0")]
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null || intent == null) return;
        var key = intent.GetStringExtra(ChatInbox.ExtraChat) ?? "";
        var title = intent.GetStringExtra(Notifier.ExtraTitle) ?? "Yaparr";
        var channel = intent.GetStringExtra(Notifier.ExtraChannel);
        var text = Android.App.RemoteInput.GetResultsFromIntent(intent)?.GetCharSequence(Notifier.ReplyKey)?.ToString()?.Trim() ?? "";
        if (key.Length == 0 || text.Length == 0) return;

        var pending = GoAsync();
        _ = Task.Run(async () =>
        {
            try
            {
                var session = AppHost.Session;
                if (session is not { Started: true } || session.FindChat(key) is not { } chat)
                {
                    Notifier.ReplyFailed(context, key, title, "Couldn't send: Yaparr is not connected. Open it to reply.");
                    return;
                }
                await Dispatcher.UIThread.InvokeAsync(() => session.SendAsync(chat, text, channelId: string.IsNullOrEmpty(channel) ? null : channel));
                Notifier.Replied(context, key, title, text);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Notifier.ReplyFailed(context, key, title, "Couldn't send: " + ex.Message);
            }
            finally { pending?.Finish(); }
        });
    }
}
