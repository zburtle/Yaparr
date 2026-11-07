using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.Media.Projection;
using Android.OS;
using Yaparr.Chat;

namespace Yaparr.AndroidApp;

/// <summary>
/// Keeps the screen share alive while Yaparr is not on the screen (which is the point: the person goes to the app they are sharing). Android
/// requires a service of the media projection kind, with a notification that is always showing, for as long as the screen is being captured;
/// the notification has a button to stop. The share itself is <see cref="ScreenShareJob"/>.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaProjection)]
[System.Runtime.Versioning.SupportedOSPlatform("android34.0")]
public class ScreenShareService : Service
{
    private const string ChannelId = "screen-share";
    private const int NotificationId = 4101;
    private const string ActionStop = "org.yaparr.android.STOP_SCREEN_SHARE";

    // What Session hands over before starting the service (the same process): the share, and what the person agreed to in Android's dialog.
    private static ScreenShareJob? s_pending;
    private static (int ResultCode, Intent Data)? s_consent;
    private static ScreenShareService? s_running;

    internal static void Begin(Context context, ScreenShareJob job, int resultCode, Intent consent)
    {
        s_pending = job;
        s_consent = (resultCode, consent);
        var intent = new Intent(context, typeof(ScreenShareService));
        context.StartForegroundService(intent);
    }

    /// <summary>Stops the share that is running, if any (the service goes with it).</summary>
    internal static void End(Context context)
    {
        if (s_running != null) s_running.StopSelf();
        else context.StopService(new Intent(context, typeof(ScreenShareService)));
    }

    private ScreenShareJob? _job;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            // Stopped from the notification: the session is told through the job, so the room and the buttons follow.
            _job?.RaiseStopped();
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        var job = s_pending;
        var consent = s_consent;
        s_pending = null;
        s_consent = null;
        if (job == null || consent == null) { StopSelf(); return StartCommandResult.NotSticky; }

        // The service has to be in the foreground, as a media projection, before the projection is made (Android 14).
        StartForeground(NotificationId, BuildNotification(), ForegroundService.TypeMediaProjection);
        s_running = this;
        _job = job;
        try
        {
            var manager = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
            var projection = manager.GetMediaProjection(consent.Value.ResultCode, consent.Value.Data)
                             ?? throw new InvalidOperationException("Android did not allow the screen to be shared.");
            job.Start(projection);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("Yaparr", "screen share could not start: " + ex.GetType().Name + ": " + ex.Message + " | " + ex.StackTrace);
            job.RaiseFailed("Could not start sharing the screen: " + ex.Message);
            StopSelf();
        }
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        if (ReferenceEquals(s_running, this)) s_running = null;
        _job?.Dispose();
        _job = null;
        base.OnDestroy();
    }

    private Notification BuildNotification()
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        if (manager.GetNotificationChannel(ChannelId) == null)
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Screen sharing", NotificationImportance.Low) { Description = "Shown while Yaparr shares your screen." });

        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop), PendingIntentFlags.Immutable)!;
        var stop = PendingIntent.GetService(this, 1, new Intent(this, typeof(ScreenShareService)).SetAction(ActionStop), PendingIntentFlags.Immutable)!;
        var icon = Icon.CreateWithResource(this, ApplicationInfo!.Icon)!;
        return new Notification.Builder(this, ChannelId)
            .SetSmallIcon(icon)
            .SetContentTitle("Yaparr is sharing your screen")
            .SetContentText("Everyone in the room can watch it.")
            .SetContentIntent(open)
            .SetOngoing(true)
            .AddAction(new Notification.Action.Builder(icon, "Stop sharing", stop).Build())
            .Build()!;
    }
}
