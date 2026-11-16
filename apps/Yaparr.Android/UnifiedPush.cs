using Android.App;
using Android.Content;
using Android.Content.PM;

namespace Yaparr.AndroidApp;

/// <summary>
/// The app's side of UnifiedPush (https://unifiedpush.org): a distributor app on the phone (ntfy, say) keeps one connection to a push server and hands each push to the
/// app that asked for it. No library: the app and the distributor talk with broadcasts, which is the whole protocol (the "Android connector" specification). The
/// distributor is only ever sent the address of this app and a token made for it; a broadcast the distributor sends back is believed only with that token
/// (<see cref="PushManager.IsOurToken"/>), because anything on the phone can send a broadcast to an app that receives them.
/// </summary>
internal static class UnifiedPush
{
    // What the app sends to a distributor.
    public const string RegisterAction = "org.unifiedpush.android.distributor.REGISTER";
    public const string UnregisterAction = "org.unifiedpush.android.distributor.UNREGISTER";
    public const string AckAction = "org.unifiedpush.android.distributor.MESSAGE_ACK";

    // What a distributor sends to the app.
    public const string NewEndpointAction = "org.unifiedpush.android.connector.NEW_ENDPOINT";
    public const string MessageAction = "org.unifiedpush.android.connector.MESSAGE";
    public const string RegistrationFailedAction = "org.unifiedpush.android.connector.REGISTRATION_FAILED";
    public const string UnregisteredAction = "org.unifiedpush.android.connector.UNREGISTERED";

    /// <summary>The distributors installed on this phone: the apps that receive a registration. Android only shows apps the manifest asks about (<c>queries</c>).</summary>
    public static IReadOnlyList<(string Package, string Label)> Distributors(Context context)
    {
        var found = new List<(string, string)>();
        try
        {
            var packages = context.PackageManager!;
            var query = new Intent(RegisterAction);
            var receivers = OperatingSystem.IsAndroidVersionAtLeast(33)
                ? packages.QueryBroadcastReceivers(query, PackageManager.ResolveInfoFlags.Of(0))
                : packages.QueryBroadcastReceivers(query, 0);
            foreach (var receiver in receivers)
            {
                var info = receiver.ActivityInfo;
                if (info?.PackageName is not { Length: > 0 } package || package == context.PackageName || found.Any(f => f.Item1 == package)) continue;
                string label = info.ApplicationInfo != null ? packages.GetApplicationLabel(info.ApplicationInfo)?.ToString() ?? package : package;
                found.Add((package, label));
            }
        }
        catch (Exception ex) when (ex is Java.Lang.RuntimeException or InvalidOperationException)
        {
            Android.Util.Log.Warn("Yaparr", "looking for push distributors: " + ex.Message);
        }
        return found;
    }

    /// <summary>Asks a distributor for an address for this app. It answers with a <see cref="NewEndpointAction"/> broadcast (a moment later, or when it has a connection).</summary>
    public static void Register(Context context, string distributor, string token)
    {
        var intent = new Intent(RegisterAction).SetPackage(distributor)
            .PutExtra("token", token)
            .PutExtra("application", context.PackageName)
            .PutExtra("message", "Yaparr: wake up when someone writes to you");
        context.SendBroadcast(intent);
    }

    /// <summary>Tells a distributor this app no longer wants pushes.</summary>
    public static void Unregister(Context context, string distributor, string token)
    {
        var intent = new Intent(UnregisterAction).SetPackage(distributor).PutExtra("token", token);
        context.SendBroadcast(intent);
    }

    /// <summary>Tells the distributor a message was handled, so it does not keep it.</summary>
    public static void Acknowledge(Context context, string distributor, string token, string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        context.SendBroadcast(new Intent(AckAction).SetPackage(distributor).PutExtra("token", token).PutExtra("id", id));
    }
}

/// <summary>
/// Receives what a UnifiedPush distributor sends: a new address for this app, a push (which is only ever "wake up"), or that the registration ended. Exported, because the
/// distributor is another app; it acts only on a broadcast that carries this app's own token.
/// </summary>
[BroadcastReceiver(Name = "org.yaparr.android.UnifiedPushReceiver", Exported = true, Enabled = true)]
[IntentFilter(new[] { UnifiedPush.NewEndpointAction, UnifiedPush.MessageAction, UnifiedPush.RegistrationFailedAction, UnifiedPush.UnregisteredAction })]
public class UnifiedPushReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null || intent?.Action is not { } action) return;
        var token = intent.GetStringExtra("token");
        if (!PushManager.IsOurToken(token)) return;
        switch (action)
        {
            case UnifiedPush.NewEndpointAction:
                PushManager.OnNewEndpoint(context, intent.GetStringExtra("endpoint"));
                break;
            case UnifiedPush.MessageAction:
                // Whatever the push says, it only means "look": nothing in it is read or shown.
                PushManager.OnWake(context);
                UnifiedPush.Acknowledge(context, PushManager.DistributorPackage(), token!, intent.GetStringExtra("id"));
                break;
            case UnifiedPush.RegistrationFailedAction:
                PushManager.OnLost(context, "The push app could not register Yaparr: " + (intent.GetStringExtra("message") ?? "no reason given") + ".");
                break;
            case UnifiedPush.UnregisteredAction:
                PushManager.OnLost(context, "The push app dropped Yaparr. Turn push notifications on again to choose one.");
                break;
        }
    }
}
