using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.OS;

namespace Yaparr.AndroidApp;

/// <summary>
/// Keeps Yaparr connected while its screen is closed: messages keep arriving (and notify), and a voice call carries on. Android asks for a service in
/// the foreground, with a notification that is always showing, for as long as an app does that; it is of the "remote messaging" kind (an app that passes
/// messages between devices), plus the microphone while a call is going on. It runs only when the person turned "Stay connected in the background" on, or
/// while there is a call. The connection itself is the process's one <see cref="Session"/> (<see cref="AppHost"/>), so the service makes one when the
/// system started the app without a screen.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("android26.0")]
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeRemoteMessaging | ForegroundService.TypeMicrophone)]
public class ConnectionService : Service
{
    private const string ChannelId = "connection";
    private const int NotificationId = 4102;
    private const string ActionLeave = "org.yaparr.android.LEAVE_CALL";
    private const string ActionTurnOff = "org.yaparr.android.STOP_BACKGROUND";

    private static ConnectionService? s_running;

    private Action<string>? _connectionHandler;
    private Session? _watching;

    /// <summary>
    /// Makes the service match what is wanted (<see cref="AppHost.KeepRunning"/>): started or updated while it is, stopped when it is not. Safe to call
    /// whenever the setting or a call changes.
    /// </summary>
    internal static void Refresh(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        if (AppHost.KeepRunning)
        {
            if (s_running is { } running) { running.Promote(); return; }
            try { context.StartForegroundService(new Intent(context, typeof(ConnectionService))); }
            catch (Exception ex) when (ex is Java.Lang.IllegalStateException or Java.Lang.SecurityException || ex.GetType().Name == "ForegroundServiceStartNotAllowedException")
            {
                // Started from the background where Android does not allow it: it starts the next time the screen is opened.
                Android.Util.Log.Warn("Yaparr", "the background service could not start: " + ex.Message);
            }
        }
        else if (s_running != null) context.StopService(new Intent(context, typeof(ConnectionService)));
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionLeave) { AppHost.Session?.LeaveVoice(); }
        else if (intent?.Action == ActionTurnOff)
        {
            AppHost.Settings.BackgroundConnection = false;
            try { AppHost.Data.SaveSettings(AppHost.Settings); } catch (IOException) { }
        }

        if (!AppHost.KeepRunning || !(AppHost.CanStartWithoutScreen || AppHost.Session != null))
        {
            StopForegroundAndSelf();
            return StartCommandResult.NotSticky;
        }

        s_running = this;
        Promote();
        // The system may have started the app just for this: there is then no screen to start the connection.
        if (AppHost.CanStartWithoutScreen && AppHost.Settings.DisplayName is { } name) _ = AppHost.StartAsync(name);
        Watch();
        return StartCommandResult.Sticky;
    }

    // The notification says how many people it is connected to.
    private void Watch()
    {
        var session = AppHost.Session;
        if (session == null || ReferenceEquals(session, _watching)) return;
        Unwatch();
        _watching = session;
        _connectionHandler = _ => Promote();
        session.ConnectionChanged += _connectionHandler;
    }

    private void Unwatch()
    {
        if (_watching != null && _connectionHandler != null) _watching.ConnectionChanged -= _connectionHandler;
        _watching = null;
        _connectionHandler = null;
    }

    // Puts the service in the foreground (or updates it): its notification, and its kind: remote messaging, and the microphone during a call.
    private void Promote()
    {
        var notification = Build();
        bool inCall = AppHost.Session?.VoiceChat != null;
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(34))
                StartForeground(NotificationId, notification, ForegroundService.TypeRemoteMessaging | (inCall ? ForegroundService.TypeMicrophone : 0));
            else if (OperatingSystem.IsAndroidVersionAtLeast(30))
                StartForeground(NotificationId, notification, inCall ? ForegroundService.TypeMicrophone : (ForegroundService)0);
            else StartForeground(NotificationId, notification);
        }
        catch (Exception ex) when (ex is Java.Lang.SecurityException or Java.Lang.IllegalStateException || ex.GetType().Name == "ForegroundServiceStartNotAllowedException")
        {
            Android.Util.Log.Warn("Yaparr", "the background service could not take the foreground: " + ex.Message);
            StopSelf();
        }
    }

    private Notification Build()
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        if (manager.GetNotificationChannel(ChannelId) == null)
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Staying connected", NotificationImportance.Low) { Description = "Shown while Yaparr stays connected in the background or is in a call." });

        var session = AppHost.Session;
        var call = session?.VoiceChat;
        string title = call != null ? "In a call" : "Yaparr is connected";
        string text = call != null ? call.Title : session?.ConnectionText is { Length: > 0 } t ? t : "Waiting for people…";

        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop), PendingIntentFlags.Immutable)!;
        var icon = Icon.CreateWithResource(this, ApplicationInfo!.Icon)!;
        var builder = new Notification.Builder(this, ChannelId)
            .SetSmallIcon(icon)
            .SetContentTitle(title)
            .SetContentText(text)
            .SetContentIntent(open)
            .SetOngoing(true)
            .SetVisibility(NotificationVisibility.Secret);
        if (call != null)
        {
            var leave = PendingIntent.GetService(this, 2, new Intent(this, typeof(ConnectionService)).SetAction(ActionLeave), PendingIntentFlags.Immutable)!;
            builder.AddAction(new Notification.Action.Builder(icon, "Leave call", leave).Build());
        }
        else
        {
            var off = PendingIntent.GetService(this, 3, new Intent(this, typeof(ConnectionService)).SetAction(ActionTurnOff), PendingIntentFlags.Immutable)!;
            builder.AddAction(new Notification.Action.Builder(icon, "Turn off", off).Build());
        }
        return builder.Build()!;
    }

    private void StopForegroundAndSelf()
    {
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
    }

    public override void OnDestroy()
    {
        if (ReferenceEquals(s_running, this)) s_running = null;
        Unwatch();
        // With no screen open either, nothing is using the connection any more.
        if (!AppHost.ScreenOpen) AppHost.Shutdown();
        base.OnDestroy();
    }
}
