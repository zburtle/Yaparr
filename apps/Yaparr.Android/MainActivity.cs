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

    private const int MicrophoneRequest = 0x6d69;

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
        App.Scanner?.OnActivityResult(requestCode, resultCode, data);
    }
}
