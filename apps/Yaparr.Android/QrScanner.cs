using Android.App;
using Android.Content;

namespace Yaparr.AndroidApp;

/// <summary>
/// Reads one QR code with the phone's camera, on the camera screen of <see cref="QrScanActivity"/>. Nothing goes through Google Play
/// services, so it works on any Android phone with a camera.
/// </summary>
public sealed class QrScanner
{
    private const int RequestCode = 4711;
    private readonly Activity _activity;
    private TaskCompletionSource<string?>? _pending;

    public QrScanner(Activity activity) => _activity = activity;

    /// <summary>The text of the code, or null if the person backed out. Throws <see cref="InvalidOperationException"/> if the camera could not be used.</summary>
    public Task<string?> ScanAsync()
    {
        _pending?.TrySetResult(null);
        var pending = _pending = new TaskCompletionSource<string?>();
        _activity.StartActivityForResult(new Intent(_activity, typeof(QrScanActivity)), RequestCode);
        return pending.Task;
    }

    /// <summary>Called by the activity that started the scan, with what the camera screen returned.</summary>
    public void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode != RequestCode) return;
        var pending = _pending;
        _pending = null;
        if (pending == null) return;
        if (resultCode == Result.Ok) pending.TrySetResult(data?.GetStringExtra(QrScanActivity.ResultKey));
        else if (data?.GetStringExtra(QrScanActivity.ErrorKey) is { } error) pending.TrySetException(new InvalidOperationException(error));
        else pending.TrySetResult(null);
    }
}
