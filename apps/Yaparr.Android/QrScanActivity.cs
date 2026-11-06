using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Camera.Core;
using AndroidX.Camera.Lifecycle;
using AndroidX.Camera.View;
using AndroidX.Core.Content;
using Java.Util.Concurrent;
using Yaparr.AppCore;
using ZXing;
using ZXing.Common;

namespace Yaparr.AndroidApp;

/// <summary>
/// The camera screen that reads a Yaparr link QR code. It uses CameraX (part of Android's own support libraries) to show the
/// camera and hand over its pictures, and ZXing to find the code in them: everything happens on the phone, and it needs no
/// Google Play services. It asks for the camera permission when it opens, and hands back the text of the first Yaparr link
/// code it sees, or nothing if the person backs out.
/// </summary>
[Activity(
    Theme = "@style/YaparrTheme",
    ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.KeyboardHidden)]
public sealed class QrScanActivity : AppCompatActivity, ImageAnalysis.IAnalyzer
{
    /// <summary>The text of the code, in the result.</summary>
    public const string ResultKey = "text";

    /// <summary>Why nothing was read, in the result, when it was not the person backing out.</summary>
    public const string ErrorKey = "error";

    private const int CameraRequest = 1;

    private PreviewView? _preview;
    private TextView? _hint;
    private IExecutorService? _analysisThread;
    private ProcessCameraProvider? _provider;
    private bool _done;

    private readonly BarcodeReaderGeneric _reader = new()
    {
        AutoRotate = true,
        Options = new DecodingOptions { TryHarder = true, PossibleFormats = new[] { BarcodeFormat.QR_CODE } },
    };

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var root = new FrameLayout(this);
        root.SetBackgroundColor(Color.Black);

        _preview = new PreviewView(this);
        root.AddView(_preview, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        _hint = new TextView(this) { Text = "Point the camera at the QR code on your computer.", Gravity = GravityFlags.Center };
        _hint.SetTextColor(Color.White);
        _hint.SetBackgroundColor(Color.Argb(150, 0, 0, 0));
        // Below the status bar, which the camera picture is drawn behind.
        int statusBar = Resources!.GetIdentifier("status_bar_height", "dimen", "android");
        int top = statusBar > 0 ? Resources.GetDimensionPixelSize(statusBar) : 0;
        _hint.SetPadding(32, top + 24, 32, 24);
        root.AddView(_hint, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top));

        var cancel = new Button(this) { Text = "Cancel" };
        cancel.Click += (_, _) => Finish();
        var cancelLayout = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Bottom | GravityFlags.CenterHorizontal);
        cancelLayout.BottomMargin = 96;
        root.AddView(cancel, cancelLayout);

        SetContentView(root);
        _analysisThread = Executors.NewSingleThreadExecutor();

        if (CheckSelfPermission(Manifest.Permission.Camera) == Permission.Granted) StartCamera();
        else RequestPermissions(new[] { Manifest.Permission.Camera }, CameraRequest);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != CameraRequest) return;
        if (grantResults.Length > 0 && grantResults[0] == Permission.Granted) StartCamera();
        else Fail("The camera is not allowed. You can allow it in the phone's settings, or paste the code instead.");
    }

    private void StartCamera()
    {
        var future = ProcessCameraProvider.GetInstance(this)!;
        var main = ContextCompat.GetMainExecutor(this)!;
        future.AddListener(new Java.Lang.Runnable(() =>
        {
            try
            {
                _provider = (ProcessCameraProvider?)future.Get();
                BindCamera();
            }
            catch (Exception ex) when (ex is Java.Lang.Exception or InvalidOperationException)
            {
                Fail("Could not start the camera: " + ex.Message);
            }
        }), main);
    }

    private void BindCamera()
    {
        if (_provider == null || _preview == null || _analysisThread == null) return;
        var preview = new Preview.Builder().Build()!;
        preview.SetSurfaceProvider(ContextCompat.GetMainExecutor(this)!, _preview.SurfaceProvider);
        var analysis = new ImageAnalysis.Builder()
            .SetBackpressureStrategy(ImageAnalysis.StrategyKeepOnlyLatest)!
            .Build()!;
        analysis.SetAnalyzer(_analysisThread, this);
        _provider.UnbindAll();
        _provider.BindToLifecycle(this, CameraSelector.DefaultBackCamera!, preview, analysis);
    }

    // Runs on the analysis thread for each picture. The first plane of the camera's pictures is the brightness of each pixel, which is all
    // a QR code needs.
    public void Analyze(IImageProxy? image)
    {
        if (image == null) return;
        try
        {
            if (_done) return;
            var plane = image.GetPlanes()![0]!;
            var buffer = plane.Buffer!;
            int rowStride = plane.RowStride, width = image.Width, height = image.Height;
            var bytes = new byte[rowStride * height];
            buffer.Get(bytes, 0, Math.Min(bytes.Length, buffer.Remaining()));
            var source = new PlanarYUVLuminanceSource(bytes, rowStride, height, 0, 0, width, height, false);
            var result = _reader.Decode(source);
            if (result == null) return;
            if (LinkQr.TryParse(result.Text, out _, out _)) RunOnUiThread(() => Succeed(result.Text));
            else RunOnUiThread(() => { if (_hint != null) _hint.Text = "That is not a Yaparr link code. Show the one from Settings, Devices, Link another device."; });
        }
        finally { image.Close(); }
    }

    private void Succeed(string text)
    {
        if (_done) return;
        _done = true;
        SetResult(Android.App.Result.Ok, new Intent().PutExtra(ResultKey, text));
        Finish();
    }

    private void Fail(string message)
    {
        if (_done) return;
        _done = true;
        SetResult(Android.App.Result.Canceled, new Intent().PutExtra(ErrorKey, message));
        Finish();
    }

    protected override void OnDestroy()
    {
        _done = true;
        try { _provider?.UnbindAll(); } catch (Java.Lang.Exception) { }
        _analysisThread?.Shutdown();
        base.OnDestroy();
    }
}
