using Yaparr.AppCore;

namespace Yaparr.AndroidApp;

// Asking, once, for notifications and push: right after the phone signs in to Yaparr Cloud (push needs that account), or at the first start after a restore from it.
public partial class MainView
{
    /// <summary>
    /// Asks whether to turn on notifications and push. One question in Yaparr's words first (so the Android dialog that follows is not a surprise), then Android's own for the
    /// permission, then push is registered with the push app on the phone (the first one, if there are several) or, with none, Firebase. Asked once: the answer, whichever it is,
    /// is remembered, and everything stays available in the settings.
    /// </summary>
    private async Task OfferNotificationsAsync()
    {
        if (_settings.NotificationsOffered || !OperatingSystem.IsAndroidVersionAtLeast(26) || MainActivity.Instance == null) return;
        if (!PushManager.TryServer(out _, out _)) return;     // push is registered with the account, so it needs the sign-in
        _settings.NotificationsOffered = true;
        _data.SaveSettings(_settings);

        if (!await ConfirmAsync("Get a notification when someone writes to you, even when Yaparr is closed?\n\nAndroid will ask you to allow notifications, and Yaparr turns on push through your Yaparr Cloud account. The server only wakes the phone: it never sees who wrote or what.", "Turn on")) return;

        _settings.NotifyMessages = true;
        _data.SaveSettings(_settings);
        _loadingSettings = true;
        NotifyCheck.IsChecked = true;
        _loadingSettings = false;
        if (!await EnsureNotificationsAsync()) return;       // Android said no: the settings page says how to allow them

        if (PushManager.State.Registered) return;
        var distributors = UnifiedPush.Distributors(Android.App.Application.Context);
        if (distributors.Count > 0) PushManager.Enable(Android.App.Application.Context, distributors[0].Package);
        else PushManager.EnableFirebase(Android.App.Application.Context);
        ShowPush();
    }
}
