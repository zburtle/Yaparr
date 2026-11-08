using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.OS;
using Yaparr.AppCore;

namespace Yaparr.AndroidApp;

/// <summary>
/// The notifications for messages that arrive while the app is not on the screen (see <see cref="NotificationPolicy"/> for which). Each chat has one,
/// which says how many new messages it has and shows the latest few once the phone is unlocked; on the lock screen it says only who and where, never the
/// words. Opening the chat, or the app, takes it away. A tap opens that chat.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("android26.0")]
internal static class Notifier
{
    private const string ChannelId = "messages";
    private const int MaxLines = 5;

    private static readonly Dictionary<string, List<string>> s_lines = new();
    private static readonly HashSet<int> s_shown = new();

    /// <summary>Whether Android lets the app show notifications (Android 13 and newer ask the person; the person can also turn them off for the app).</summary>
    public static bool Allowed(Context context)
    {
        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        if (manager == null || !manager.AreNotificationsEnabled()) return false;
        return !OperatingSystem.IsAndroidVersionAtLeast(33) || context.CheckSelfPermission("android.permission.POST_NOTIFICATIONS") == Permission.Granted;
    }

    // A number that is the same for the same chat in every run of the app (a string's hash code is not).
    private static int IdOf(string chatKey)
    {
        uint hash = 2166136261;
        foreach (char c in chatKey) hash = (hash ^ c) * 16777619;
        return 5000 + (int)(hash & 0x3FFFFFFF);
    }

    private static NotificationManager? Manager(Context context)
    {
        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        if (manager != null && manager.GetNotificationChannel(ChannelId) == null)
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Messages", NotificationImportance.High) { Description = "Direct messages, mentions, replies and rooms set to notify for every message." });
        return manager;
    }

    /// <summary>Shows (or updates) the notification of a chat for a new message.</summary>
    public static void Message(Context context, string chatKey, string chatTitle, string sender, string body, bool isDirect)
    {
        if (!Allowed(context) || Manager(context) is not { } manager) return;
        int id = IdOf(chatKey);
        List<string> lines;
        lock (s_lines)
        {
            if (!s_lines.TryGetValue(chatKey, out lines!)) s_lines[chatKey] = lines = new List<string>();
            lines.Add(isDirect ? body : sender + ": " + body);
            while (lines.Count > MaxLines) lines.RemoveAt(0);
            s_shown.Add(id);
        }

        int count;
        string[] latest;
        lock (s_lines) { count = lines.Count; latest = lines.ToArray(); }
        string where = NotificationPolicy.LockScreenText(sender, chatTitle, isDirect);
        string summary = count == 1 ? "New message" : count + " new messages";

        var open = PendingIntent.GetActivity(context, id, OpenChat(context, chatKey), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        var icon = Icon.CreateWithResource(context, context.ApplicationInfo!.Icon)!;

        // What shows on the lock screen: who and where, not the words.
        var publicVersion = new Notification.Builder(context, ChannelId)
            .SetSmallIcon(icon)
            .SetContentTitle(where)
            .SetContentText(summary)
            .Build()!;

        var builder = new Notification.Builder(context, ChannelId)
            .SetSmallIcon(icon)
            .SetContentTitle(chatTitle)
            .SetContentText(latest[^1])
            .SetStyle(new Notification.BigTextStyle().BigText(string.Join("\n", latest))!.SetSummaryText(summary))
            .SetCategory(Notification.CategoryMessage)
            .SetAutoCancel(true)
            .SetContentIntent(open)
            .SetOnlyAlertOnce(false)
            .SetVisibility(NotificationVisibility.Private)
            .SetPublicVersion(publicVersion);
        manager.Notify(id, builder.Build()!);
    }

    private static Intent OpenChat(Context context, string chatKey) =>
        new Intent(context, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop).PutExtra(ChatInbox.ExtraChat, chatKey);

    /// <summary>The chat was opened (or the app was): its notification, and what it counted, go away.</summary>
    public static void Clear(Context context, string chatKey)
    {
        int id = IdOf(chatKey);
        lock (s_lines) { s_lines.Remove(chatKey); s_shown.Remove(id); }
        ((NotificationManager?)context.GetSystemService(Context.NotificationService))?.Cancel(id);
    }

    public static void ClearAll(Context context)
    {
        int[] ids;
        lock (s_lines) { ids = s_shown.ToArray(); s_lines.Clear(); s_shown.Clear(); }
        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        foreach (var id in ids) manager?.Cancel(id);
    }

    /// <summary>A notification that is only there to show that they work: from the settings, so that the permission and the lock screen can be tried.</summary>
    public static bool Test(Context context)
    {
        if (!Allowed(context) || Manager(context) is not { } manager) return false;
        const int id = 4999;
        var open = PendingIntent.GetActivity(context, id, new Intent(context, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop), PendingIntentFlags.Immutable)!;
        var icon = Icon.CreateWithResource(context, context.ApplicationInfo!.Icon)!;
        var publicVersion = new Notification.Builder(context, ChannelId).SetSmallIcon(icon).SetContentTitle("Yaparr").SetContentText("A test notification").Build()!;
        manager.Notify(id, new Notification.Builder(context, ChannelId)
            .SetSmallIcon(icon)
            .SetContentTitle("Notifications work")
            .SetContentText("This is what a message looks like. Tap to open Yaparr.")
            .SetCategory(Notification.CategoryMessage)
            .SetAutoCancel(true)
            .SetContentIntent(open)
            .SetVisibility(NotificationVisibility.Private)
            .SetPublicVersion(publicVersion)
            .Build()!);
        return true;
    }
}
