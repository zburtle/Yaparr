using Android.Content;
using Yaparr.AppCore;

namespace Yaparr.AndroidApp;

/// <summary>
/// Reads what this release is offered with (<c>yaparr.release.json</c>, the same file the desktop ships) from the package's assets, since a phone has no
/// folder beside the program to put it in. The backup server's address on the sign-in screens comes from it. A missing or unusable file means no default,
/// and the app starts just the same.
/// </summary>
internal static class ReleaseAsset
{
    public static void Load(Context context)
    {
        try
        {
            using var stream = context.Assets?.Open(ReleaseConfig.FileName);
            if (stream == null) return;
            using var reader = new StreamReader(stream);
            ReleaseConfig.Use(ReleaseConfig.Parse(reader.ReadToEnd()));
        }
        catch (Exception ex) when (ex is IOException or Java.IO.IOException or Java.Lang.RuntimeException)
        {
            ReleaseConfig.Use(ReleaseConfig.None);
        }
    }
}
