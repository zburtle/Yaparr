using Android.App;
using Android.Content;
using Android.Gms.Extensions;
using Firebase.Messaging;

// The Firebase binding marks the members used here "deprecated" although they are the ones Firebase documents; there is no other way to reach them.
#pragma warning disable CS0618, CS0672

namespace Yaparr.AndroidApp;

/// <summary>
/// The Firebase Cloud Messaging side of push, for phones with no UnifiedPush app. It is a fallback and does the same small job: the server sends a push with nothing in it,
/// and <see cref="PushManager.OnWake"/> brings the connection up. Messages never travel through Google, only the word "wake up" does.
/// </summary>
internal static class FirebasePush
{
    /// <summary>This phone's Firebase address (the token the server pushes to), or null when Firebase is not usable here (no Google services on the phone, or no network).</summary>
    public static async Task<string?> TokenAsync()
    {
        try
        {
            var token = await FirebaseMessaging.Instance.GetToken().AsAsync<Java.Lang.String>();
            return token?.ToString();
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or Java.Util.Concurrent.ExecutionException or InvalidOperationException or TaskCanceledException)
        {
            Android.Util.Log.Warn("Yaparr", "Firebase token: " + ex.Message);
            return null;
        }
    }

    /// <summary>Forgets this phone's Firebase address, so what the server holds stops working.</summary>
    public static void Forget()
    {
        try { FirebaseMessaging.Instance.DeleteToken(); }
        catch (Exception ex) when (ex is Java.Lang.Exception or InvalidOperationException) { Android.Util.Log.Warn("Yaparr", "Firebase forget: " + ex.Message); }
    }
}

/// <summary>Receives what Firebase delivers: a new token (the server must be told) and a push, which is only ever "wake up": nothing in it is read.</summary>
[Service(Name = "org.yaparr.android.FirebasePushService", Exported = false)]
[IntentFilter(new[] { "com.google.firebase.MESSAGING_EVENT" })]
public class FirebasePushService : FirebaseMessagingService
{
    public override void OnNewToken(string token)
    {
        base.OnNewToken(token);
        PushManager.OnNewFirebaseToken(this, token);
    }

    public override void OnMessageReceived(RemoteMessage message)
    {
        base.OnMessageReceived(message);
        if (PushManager.State.Route == Yaparr.AppCore.PushRoute.Fcm) PushManager.OnWake(this);
    }
}
