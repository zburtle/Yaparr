using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Avalonia;
using Avalonia.Android;

// Yaparr talks to other peers over UDP: that is all the network access it needs.
[assembly: UsesPermission(Android.Manifest.Permission.Internet)]
[assembly: UsesPermission(Android.Manifest.Permission.AccessNetworkState)]
[assembly: UsesPermission(Android.Manifest.Permission.ChangeWifiMulticastState)]

// The microphone is for voice channels, asked for the first time you join one.
[assembly: UsesPermission(Android.Manifest.Permission.RecordAudio)]
[assembly: UsesPermission(Android.Manifest.Permission.ModifyAudioSettings)]
[assembly: UsesFeature("android.hardware.microphone", Required = false)]

// Sharing the screen runs as a foreground service of the media projection kind (Android 14 and newer), with a notification that says so
// and has a button to stop; the notification needs the permission to show on Android 13 and newer, asked for with the first share.
[assembly: UsesPermission(Android.Manifest.Permission.ForegroundService)]
[assembly: UsesPermission("android.permission.FOREGROUND_SERVICE_MEDIA_PROJECTION")]
[assembly: UsesPermission(Android.Manifest.Permission.PostNotifications)]

// The camera is only for reading a link QR code, and the app works without one.
[assembly: UsesPermission(Android.Manifest.Permission.Camera)]
[assembly: UsesFeature("android.hardware.camera", Required = false)]
[assembly: UsesFeature("android.hardware.camera.autofocus", Required = false)]

namespace Yaparr.AndroidApp;

/// <summary>Starts Avalonia when the process starts, before any screen. Its label and icon are what the installer, the settings and the app list show.</summary>
[Application(Label = "Yaparr", Icon = "@drawable/icon")]
public class AndroidApp : AvaloniaAndroidApplication<App>
{
    public AndroidApp(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer) { }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder).WithInterFont();
}

// Yaparr links open in this app: yaparr://join/<invite> and yaparr://chat/<peer id>, and the https://yaparr.com/join#<invite> and /chat#<peer id> forms
// that people share. The https ones are claimed for the app only once yaparr.com vouches for it (a digital asset links file there); until then they open
// in the browser, whose page hands them to the app.
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "yaparr", DataHost = "join")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "yaparr", DataHost = "chat")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "https", DataHost = "yaparr.com", DataPathPrefix = "/join", AutoVerify = true)]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "https", DataHost = "yaparr.com", DataPathPrefix = "/chat", AutoVerify = true)]
[Activity(
    Label = "Yaparr",
    Icon = "@drawable/icon",
    Theme = "@style/YaparrTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.UiMode | ConfigChanges.KeyboardHidden)]
public class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Instance = this;
        App.Scanner = new QrScanner(this);
        base.OnCreate(savedInstanceState);
        // Opened by a link; not again when the activity is made anew after a turn of the screen.
        if (savedInstanceState == null) LinkInbox.Offer(Intent?.DataString);
    }

    // Already open when a link is tapped: the same activity gets it.
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        LinkInbox.Offer(intent?.DataString);
    }

    /// <summary>The screen that is open, for what needs an activity (asking for the microphone, the audio service).</summary>
    public static MainActivity? Instance { get; private set; }

    private static TaskCompletionSource<bool>? s_micRequest;

    /// <summary>Asks for the microphone if it has not been allowed yet. False if the person says no.</summary>
    public Task<bool> RequestMicrophoneAsync()
    {
        if (CheckSelfPermission(Android.Manifest.Permission.RecordAudio) == Permission.Granted) return Task.FromResult(true);
        s_micRequest?.TrySetResult(false);
        var request = s_micRequest = new TaskCompletionSource<bool>();
        RequestPermissions(new[] { Android.Manifest.Permission.RecordAudio }, MicrophoneRequest);
        return request.Task;
    }

    private const int MicrophoneRequest = 0x6d69, PickRequest = 0x7069, SaveRequest = 0x7376, PickFileRequest = 0x7066, CaptureRequest = 0x6361, NotificationsRequest = 0x6e74;
    private static TaskCompletionSource<(int ResultCode, Intent Data)?>? s_capture;

    /// <summary>
    /// Asks Android to share the screen: its own dialog, where the person chooses one app or the whole screen (Android 14 and newer). Null if they say no.
    /// Also asks, once, to be allowed to show the notification that goes with a share (the share does not depend on the answer).
    /// </summary>
    public async Task<(int ResultCode, Intent Data)?> RequestScreenCaptureAsync()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications }, NotificationsRequest);
        s_capture?.TrySetResult(null);
        var request = s_capture = new TaskCompletionSource<(int, Intent)?>();
        if (GetSystemService(MediaProjectionService) is not Android.Media.Projection.MediaProjectionManager manager) return null;
        try { StartActivityForResult(manager.CreateScreenCaptureIntent(), CaptureRequest); }
        catch (ActivityNotFoundException) { request.TrySetResult(null); }
        return await request.Task;
    }
    private static TaskCompletionSource<string?>? s_pickFile;
    private static TaskCompletionSource<(byte[] Bytes, string Name)?>? s_pick;
    private static TaskCompletionSource<bool>? s_save;
    private static byte[]? s_saving;

    /// <summary>Opens the system's chooser for a picture and returns it; null if the person backs out or the file cannot be read. The chooser needs no permission: the person hands over the one file.</summary>
    public Task<(byte[] Bytes, string Name)?> PickPictureAsync() => PickBytesAsync("image/*", 40 * 1024 * 1024);

    private static int s_pickLimit = 40 * 1024 * 1024;

    /// <summary>The same for a file of any kind (<paramref name="mime"/> narrows what is offered), up to <paramref name="maxBytes"/>.</summary>
    public Task<(byte[] Bytes, string Name)?> PickBytesAsync(string mime, int maxBytes)
    {
        s_pickLimit = maxBytes;
        s_pick?.TrySetResult(null);
        var request = s_pick = new TaskCompletionSource<(byte[] Bytes, string Name)?>();
        var intent = new Intent(Intent.ActionGetContent).SetType(mime).AddCategory(Intent.CategoryOpenable);
        try { StartActivityForResult(intent, PickRequest); }
        catch (ActivityNotFoundException) { request.TrySetResult(null); }
        return request.Task;
    }

    /// <summary>Asks where to save a file (the system's "save as") and writes it there. False if the person backs out.</summary>
    public Task<bool> SaveFileAsync(byte[] bytes, string fileName, string mime)
    {
        s_save?.TrySetResult(false);
        var request = s_save = new TaskCompletionSource<bool>();
        s_saving = bytes;
        var intent = new Intent(Intent.ActionCreateDocument).SetType(mime).AddCategory(Intent.CategoryOpenable).PutExtra(Intent.ExtraTitle, fileName);
        try { StartActivityForResult(intent, SaveRequest); }
        catch (ActivityNotFoundException) { request.TrySetResult(false); }
        return request.Task;
    }

    /// <summary>Opens the system's file chooser and copies what is chosen into the app's cache, returning the path; null if the person backs out or it cannot be read.</summary>
    public Task<string?> PickFileAsync(string mime)
    {
        s_pickFile?.TrySetResult(null);
        var request = s_pickFile = new TaskCompletionSource<string?>();
        var intent = new Intent(Intent.ActionOpenDocument).SetType(mime).AddCategory(Intent.CategoryOpenable);
        try { StartActivityForResult(intent, PickFileRequest); }
        catch (ActivityNotFoundException) { request.TrySetResult(null); }
        return request.Task;
    }

    private void FinishPickFile(Result resultCode, Intent? data)
    {
        var request = s_pickFile;
        if (request == null) return;
        if (resultCode != Result.Ok || data?.Data is not { } uri) { request.TrySetResult(null); return; }
        _ = Task.Run(() =>
        {
            try
            {
                var path = System.IO.Path.Combine(CacheDir?.AbsolutePath ?? System.IO.Path.GetTempPath(), "restore-" + Guid.NewGuid().ToString("N") + ".zip");
                using (var input = ContentResolver?.OpenInputStream(uri))
                {
                    if (input == null) { request.TrySetResult(null); return; }
                    using var output = File.Create(path);
                    input.CopyTo(output);
                }
                request.TrySetResult(path);
            }
            catch (Exception ex) when (ex is Java.IO.IOException or IOException or Java.Lang.SecurityException) { request.TrySetResult(null); }
        });
    }

    /// <summary>Starts the app again from the beginning (after a restore, so it comes up with the restored account).</summary>
    public void RestartApp()
    {
        var intent = PackageManager?.GetLaunchIntentForPackage(PackageName ?? "");
        if (intent != null)
        {
            intent.AddFlags(ActivityFlags.ClearTop | ActivityFlags.NewTask | ActivityFlags.ClearTask);
            StartActivity(intent);
        }
        Process.KillProcess(Process.MyPid());
    }

    private void FinishPick(Result resultCode, Intent? data)
    {
        var request = s_pick;
        if (request == null) return;
        if (resultCode != Result.Ok || data?.Data is not { } uri) { request.TrySetResult(null); return; }
        _ = Task.Run(() =>
        {
            try
            {
                string name = "picture";
                using (var cursor = ContentResolver?.Query(uri, new[] { "_display_name" }, null, null, null))
                    if (cursor != null && cursor.MoveToFirst() && cursor.GetString(0) is { Length: > 0 } shown) name = shown;
                using var input = ContentResolver?.OpenInputStream(uri);
                if (input == null) { request.TrySetResult(null); return; }
                using var ms = new MemoryStream();
                var buffer = new byte[81920];
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (ms.Length + n > s_pickLimit) { request.TrySetResult(null); return; }
                    ms.Write(buffer, 0, n);
                }
                request.TrySetResult((ms.ToArray(), name));
            }
            catch (Exception ex) when (ex is Java.IO.IOException or IOException or Java.Lang.SecurityException) { request.TrySetResult(null); }
        });
    }

    private void FinishSave(Result resultCode, Intent? data)
    {
        var request = s_save;
        var bytes = s_saving;
        s_saving = null;
        if (request == null) return;
        if (resultCode != Result.Ok || data?.Data is not { } uri || bytes == null) { request.TrySetResult(false); return; }
        _ = Task.Run(() =>
        {
            try
            {
                using var output = ContentResolver?.OpenOutputStream(uri);
                if (output == null) { request.TrySetResult(false); return; }
                output.Write(bytes, 0, bytes.Length);
                request.TrySetResult(true);
            }
            catch (Exception ex) when (ex is Java.IO.IOException or IOException or Java.Lang.SecurityException) { request.TrySetResult(false); }
        });
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == MicrophoneRequest) s_micRequest?.TrySetResult(grantResults.Length > 0 && grantResults[0] == Permission.Granted);
    }

    private Android.Net.Wifi.WifiManager.MulticastLock? _multicast;

    // Android drops the multicast packets that finding people on the same Wi-Fi uses, unless the app asks to receive them. While the
    // app is open it does.
    protected override void OnResume()
    {
        base.OnResume();
        try
        {
            if (GetSystemService(WifiService) is Android.Net.Wifi.WifiManager wifi)
            {
                _multicast ??= wifi.CreateMulticastLock("yaparr");
                _multicast?.SetReferenceCounted(false);
                if (_multicast is { IsHeld: false }) _multicast.Acquire();
            }
        }
        catch (Java.Lang.SecurityException) { }
    }

    protected override void OnPause()
    {
        try { if (_multicast is { IsHeld: true }) _multicast.Release(); }
        catch (Java.Lang.RuntimeException) { }
        base.OnPause();
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == PickRequest) { FinishPick(resultCode, data); return; }
        if (requestCode == SaveRequest) { FinishSave(resultCode, data); return; }
        if (requestCode == PickFileRequest) { FinishPickFile(resultCode, data); return; }
        if (requestCode == CaptureRequest) { s_capture?.TrySetResult(resultCode == Result.Ok && data != null ? ((int)resultCode, data) : null); return; }
        App.Scanner?.OnActivityResult(requestCode, resultCode, data);
    }
}
