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
// That permission makes Android assume the phone needs Wi-Fi, and the QR scanner being locked to portrait makes it assume a portrait screen. Neither is needed (the
// app works over mobile data, Ethernet or a VPN, and in any orientation), so say so, or Play hides the app from phones, tablets and desktops without them.
[assembly: UsesFeature("android.hardware.wifi", Required = false)]
[assembly: UsesFeature("android.hardware.screen.portrait", Required = false)]

// The microphone is for voice channels, asked for the first time you join one.
[assembly: UsesPermission(Android.Manifest.Permission.RecordAudio)]
[assembly: UsesPermission(Android.Manifest.Permission.ModifyAudioSettings)]
[assembly: UsesFeature("android.hardware.microphone", Required = false)]

// Sharing the screen runs as a foreground service of the media projection kind (Android 14 and newer), with a notification that says so
// and has a button to stop; the notification needs the permission to show on Android 13 and newer, asked for with the first share.
[assembly: UsesPermission(Android.Manifest.Permission.ForegroundService)]
[assembly: UsesPermission("android.permission.FOREGROUND_SERVICE_MEDIA_PROJECTION")]
[assembly: UsesPermission(Android.Manifest.Permission.PostNotifications)]

// "Stay connected in the background" keeps the connection in a foreground service of the remote messaging kind (and the microphone kind during a call).
[assembly: UsesPermission("android.permission.FOREGROUND_SERVICE_REMOTE_MESSAGING")]
[assembly: UsesPermission("android.permission.FOREGROUND_SERVICE_MICROPHONE")]

// Updating the app from its own releases hands the downloaded package to Android's installer, which asks for the person's confirmation. Android asks
// them once to allow Yaparr to install packages.
#if !PLAY_STORE   // Google Play updates the app itself and does not allow the permission.
[assembly: UsesPermission(Android.Manifest.Permission.RequestInstallPackages)]
#endif

// The camera is only for reading a link QR code, and the app works without one.
[assembly: UsesPermission(Android.Manifest.Permission.Camera)]
[assembly: UsesFeature("android.hardware.camera", Required = false)]
[assembly: UsesFeature("android.hardware.camera.autofocus", Required = false)]

namespace Yaparr.AndroidApp;

/// <summary>Starts Avalonia when the process starts, before any screen. Its label and icon are what the installer, the settings and the app list show.</summary>
[Application(Label = "Yaparr", Icon = "@mipmap/ic_launcher", RoundIcon = "@mipmap/ic_launcher_round")]
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
// The button on a backup server's "You're in" page (yaparr://backup-signin/<code>/<server>), which signs this phone in once the person agrees.
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "yaparr", DataHost = "backup-signin")]
// The browser coming back after signing in to a backup server in it (yaparr://signed-in?code=...&state=...), which a phone cannot listen for on a port.
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "yaparr", DataHost = "signed-in")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "https", DataHost = "yaparr.com", DataPathPrefix = "/join", AutoVerify = true)]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "https", DataHost = "yaparr.com", DataPathPrefix = "/chat", AutoVerify = true)]
[Activity(
    Label = "Yaparr",
    Icon = "@mipmap/ic_launcher",
    RoundIcon = "@mipmap/ic_launcher_round",
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
        AppHost.ScreenOpen = true;
        ReleaseAsset.Load(this);
        App.Scanner = new QrScanner(this);
        base.OnCreate(savedInstanceState);
        // Opened by a link or a notification; not again when the activity is made anew after a turn of the screen.
        if (savedInstanceState == null)
        {
            LinkInbox.Offer(Intent?.DataString);
            ChatInbox.Offer(Intent?.GetStringExtra(ChatInbox.ExtraChat));
        }
    }

    protected override void OnDestroy()
    {
        AppHost.ScreenOpen = false;
        if (ReferenceEquals(Instance, this)) Instance = null;
        // The screen is gone for good (swiped away, not turned): with nothing asking to stay connected, the connection ends.
        if (IsFinishing && !AppHost.KeepRunning) AppHost.Shutdown();
        base.OnDestroy();
    }

    // Already open when a link is tapped: the same activity gets it.
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        LinkInbox.Offer(intent?.DataString);
        ChatInbox.Offer(intent?.GetStringExtra(ChatInbox.ExtraChat));
    }

    /// <summary>The activity came back to the front (from another app, or the lock screen).</summary>
    public static event Action? Resumed;

    /// <summary>Whether the on-screen keyboard is showing, as Android says (null when this version cannot say).</summary>
    public bool? KeyboardShown =>
        OperatingSystem.IsAndroidVersionAtLeast(30) ? Window?.DecorView?.RootWindowInsets?.IsVisible(WindowInsets.Type.Ime()) : null;

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

    private const int MicrophoneRequest = 0x6d69, PickRequest = 0x7069, SaveRequest = 0x7376, PickFileRequest = 0x7066, PickShareRequest = 0x7073, CaptureRequest = 0x6361, NotificationsRequest = 0x6e74;
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
        var copyFrom = s_savingPath;
        s_saving = null;
        s_savingPath = null;
        if (request == null) return;
        if (resultCode != Result.Ok || data?.Data is not { } uri || (bytes == null && copyFrom == null)) { request.TrySetResult(false); return; }
        _ = Task.Run(() =>
        {
            try
            {
                using var output = ContentResolver?.OpenOutputStream(uri);
                if (output == null) { request.TrySetResult(false); return; }
                if (copyFrom != null)
                {
                    // A file of any size (a received file): copied piece by piece, never held in memory.
                    using var input = new FileStream(copyFrom, FileMode.Open, FileAccess.Read, FileShare.Read);
                    input.CopyTo(output, 1 << 20);
                }
                else output.Write(bytes!, 0, bytes!.Length);
                request.TrySetResult(true);
            }
            catch (Exception ex) when (ex is Java.IO.IOException or IOException or Java.Lang.SecurityException) { request.TrySetResult(false); }
        });
    }

    private static string? s_savingPath;
    private static TaskCompletionSource<(string Address, string Name)?>? s_pickShare;

    /// <summary>Asks where to save a copy of a file that is on this phone (a received file) and copies it there, in pieces. False if the person backs out or it cannot be written.</summary>
    public Task<bool> SaveCopyAsync(string path, string fileName)
    {
        s_save?.TrySetResult(false);
        var request = s_save = new TaskCompletionSource<bool>();
        s_saving = null;
        s_savingPath = path;
        var intent = new Intent(Intent.ActionCreateDocument).SetType("application/octet-stream").AddCategory(Intent.CategoryOpenable).PutExtra(Intent.ExtraTitle, fileName);
        try { StartActivityForResult(intent, SaveRequest); }
        catch (ActivityNotFoundException) { request.TrySetResult(false); }
        return request.Task;
    }

    /// <summary>
    /// Copies a received file into the phone's Downloads folder (a folder called Yaparr in it), where the Files app and other apps can see it, in pieces. Returns where it went
    /// ("Downloads/Yaparr/name"), or null when it could not be done. Android 10 and newer need no permission for this; older ones use "Save as…" instead.
    /// </summary>
    public async Task<string?> SaveToDownloadsAsync(string path, string fileName) => (await SaveToDownloadsWithAddressAsync(path, fileName))?.Where;

    /// <summary>Like <see cref="SaveToDownloadsAsync"/>, and also gives the content address of the copy (so it can be opened).</summary>
    public Task<(string Where, string Address)?> SaveToDownloadsWithAddressAsync(string path, string fileName)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29)) return Task.FromResult<(string, string)?>(null);
        return Task.Run<(string, string)?>(() =>
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(29)) return null;
            try
            {
                var values = new ContentValues();
                values.Put(Android.Provider.MediaStore.IMediaColumns.DisplayName, fileName);
                values.Put(Android.Provider.MediaStore.IMediaColumns.MimeType, "application/octet-stream");
                values.Put(Android.Provider.MediaStore.IMediaColumns.RelativePath, "Download/Yaparr");
                var uri = ContentResolver?.Insert(Android.Provider.MediaStore.Downloads.ExternalContentUri!, values);
                if (uri == null) return null;
                using (var output = ContentResolver?.OpenOutputStream(uri))
                {
                    if (output == null) return null;
                    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    input.CopyTo(output, 1 << 20);
                }
                // The name Android gave it, which has a number added when the name was taken.
                string shown = fileName;
                using (var cursor = ContentResolver?.Query(uri, new[] { "_display_name" }, null, null, null))
                    if (cursor != null && cursor.MoveToFirst() && cursor.GetString(0) is { Length: > 0 } actual) shown = actual;
                return ("Downloads/Yaparr/" + shown, uri.ToString() ?? "");
            }
            catch (Exception ex) when (ex is Java.IO.IOException or IOException or Java.Lang.SecurityException or Java.Lang.IllegalStateException) { return null; }
        });
    }

    /// <summary>Opens a file (a content address) in whatever app on the phone handles its type. False when there is no such app or it cannot be read.</summary>
    public bool OpenFile(string address, string fileName)
    {
        try
        {
            if (Android.Net.Uri.Parse(address) is not { } uri) return false;
            var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
            var mime = (extension.Length > 0 ? Android.Webkit.MimeTypeMap.Singleton?.GetMimeTypeFromExtension(extension) : null) ?? "*/*";
            var intent = new Intent(Intent.ActionView).SetDataAndType(uri, mime).AddFlags(ActivityFlags.GrantReadUriPermission);
            StartActivity(Intent.CreateChooser(intent, "Open with"));
            return true;
        }
        catch (Exception ex) when (ex is ActivityNotFoundException or Java.Lang.SecurityException) { return false; }
    }

    /// <summary>
    /// Opens the system's chooser for a file of any kind to share, and returns its address (not its bytes: the file stays where it is and is read as people download it) and name.
    /// Android is asked to keep the permission to read it, so it can still be sent after the app is closed and opened again. Null if the person backs out.
    /// </summary>
    public Task<(string Address, string Name)?> PickSharedFileAsync()
    {
        s_pickShare?.TrySetResult(null);
        var request = s_pickShare = new TaskCompletionSource<(string, string)?>();
        var intent = new Intent(Intent.ActionOpenDocument).SetType("*/*").AddCategory(Intent.CategoryOpenable)
            .AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission);
        try { StartActivityForResult(intent, PickShareRequest); }
        catch (ActivityNotFoundException) { request.TrySetResult(null); }
        return request.Task;
    }

    private void FinishPickShare(Result resultCode, Intent? data)
    {
        var request = s_pickShare;
        if (request == null) return;
        if (resultCode != Result.Ok || data?.Data is not { } uri) { request.TrySetResult(null); return; }
        try { ContentResolver?.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission); }
        catch (Java.Lang.SecurityException) { }
        string name = "file";
        try
        {
            using var cursor = ContentResolver?.Query(uri, new[] { "_display_name" }, null, null, null);
            if (cursor != null && cursor.MoveToFirst() && cursor.GetString(0) is { Length: > 0 } shown) name = shown;
        }
        catch (Exception ex) when (ex is Java.Lang.SecurityException or Java.Lang.IllegalArgumentException) { }
        request.TrySetResult((uri.ToString() ?? "", name));
    }

    /// <summary>Opens a file the person chose to share (its content address) for reading, or null when it is gone or not readable. A file read through a pipe (some cloud providers) has no length and cannot be shared.</summary>
    public static Stream? OpenContent(string address)
    {
        try
        {
            var resolver = Instance?.ContentResolver;
            if (Android.Net.Uri.Parse(address) is not { } uri) return null;
            using var descriptor = resolver?.OpenFileDescriptor(uri, "r");
            if (descriptor == null) return null;
            var stream = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)descriptor.DetachFd(), ownsHandle: true), FileAccess.Read);
            _ = stream.Length;
            return stream;
        }
        catch (Exception ex) when (ex is Java.IO.IOException or IOException or Java.Lang.SecurityException or NotSupportedException or Java.Lang.IllegalArgumentException) { return null; }
    }

    /// <summary>Where received files are kept: the app's own folder on shared storage, which needs no permission and is removed with the app.</summary>
    public string ReceivedFilesDirectory
    {
        get
        {
            var dir = System.IO.Path.Combine(GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir?.AbsolutePath ?? System.IO.Path.GetTempPath(), "received");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == MicrophoneRequest) s_micRequest?.TrySetResult(grantResults.Length > 0 && grantResults[0] == Permission.Granted);
        if (requestCode == NotificationsRequest) s_notifications?.TrySetResult(grantResults.Length > 0 && grantResults[0] == Permission.Granted);
    }

    private static TaskCompletionSource<bool>? s_notifications;

    /// <summary>Asks to be allowed to show notifications (Android 13 and newer ask; older ones allow them). False if the person says no or has turned them off for the app.</summary>
    public async Task<bool> RequestNotificationsAsync()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) || CheckSelfPermission(Android.Manifest.Permission.PostNotifications) == Permission.Granted)
            return (GetSystemService(NotificationService) as NotificationManager)?.AreNotificationsEnabled() ?? false;
        s_notifications?.TrySetResult(false);
        var request = s_notifications = new TaskCompletionSource<bool>();
        RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications }, NotificationsRequest);
        return await request.Task;
    }

    /// <summary>Opens Android's list of apps that are not limited in the background, where the person can let Yaparr stay connected on a phone that stops apps.</summary>
    public void OpenBatterySettings()
    {
        try { StartActivity(new Intent(Android.Provider.Settings.ActionIgnoreBatteryOptimizationSettings)); }
        catch (ActivityNotFoundException) { }
    }

    private Android.Net.Wifi.WifiManager.MulticastLock? _multicast;

    // Android drops the multicast packets that finding people on the same Wi-Fi uses, unless the app asks to receive them. While the
    // app is open it does.
    protected override void OnResume()
    {
        base.OnResume();
        AppHost.InForeground = true;
        Resumed?.Invoke();
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) Notifier.ClearAll(this);
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
        AppHost.InForeground = false;
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
        if (requestCode == PickShareRequest) { FinishPickShare(resultCode, data); return; }
        if (requestCode == CaptureRequest) { s_capture?.TrySetResult(resultCode == Result.Ok && data != null ? ((int)resultCode, data) : null); return; }
        App.Scanner?.OnActivityResult(requestCode, resultCode, data);
    }
}
