using System.Net.Http;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Yaparr.Updates;

namespace Yaparr.AndroidApp;

/// <summary>
/// Updates the app from its own releases on GitHub: finds a newer package, downloads it, checks it against the checksum that was published with it, and
/// hands it to Android's installer. Android always asks the person to confirm an install, and refuses a package that is not signed with the same key as
/// the installed app, so a package from anywhere else cannot replace the app. It uses nothing but what Android itself provides.
/// </summary>
public static class AppUpdater
{
    /// <summary>The repository whose releases hold the packages.</summary>
    public const string Repository = "zburtle/Yaparr";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Where the steps of an update are written, so a failed one can be understood afterwards.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>Told how an install ended, when Android says (not when it worked: the app is then replaced and closed).</summary>
    public static event Action<string>? InstallFailed;

    /// <summary>True for a build made for trying on a phone: it is signed with a key that is not the release key, so a release could not replace it.</summary>
    public static bool IsTestBuild =>
        (Application.Context.ApplicationInfo?.Flags & ApplicationInfoFlags.Debuggable) != 0;

    public static string CurrentVersionText
    {
        get
        {
            try
            {
                var context = Application.Context;
                return context.PackageManager?.GetPackageInfo(context.PackageName ?? "", 0)?.VersionName ?? "";
            }
            catch (Exception ex) when (ex is PackageManager.NameNotFoundException or Java.Lang.RuntimeException) { return ""; }
        }
    }

    public static async Task<AndroidUpdate?> CheckAsync(CancellationToken ct = default)
    {
        if (!AndroidReleases.TryParseVersion(CurrentVersionText, out var current)) return null;
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases?per_page=30", ct);
        if (!response.IsSuccessStatusCode)
        {
            Log?.Invoke($"update: GitHub answered {(int)response.StatusCode} when asked for the releases");
            return null;
        }
        var update = AndroidReleases.Newest(await response.Content.ReadAsStringAsync(ct), current, Repository);
        Log?.Invoke(update == null ? $"update: {current} is the newest" : $"update: {update.Tag} is available ({update.Name}, checksum {update.Sha256[..12]}...)");
        return update;
    }

    /// <summary>Downloads the package into the app's own private cache, and checks it. Nothing is installed that does not match what was published.</summary>
    /// <param name="progress">Told how far the download is, from 0 to 1.</param>
    public static async Task<string> DownloadAsync(AndroidUpdate update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!UpdateVerifier.IsTrustedDownload(update.DownloadUrl, Repository))
            throw new InvalidDataException("The download address is not one of this project's releases.");

        var dir = Path.Combine(Application.Context.CacheDir!.AbsolutePath, "updates");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir)) { try { File.Delete(old); } catch (IOException) { } }
        var path = Path.Combine(dir, "Yaparr-" + update.Version + ".apk");
        var partial = path + ".part";
        try
        {
            using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(partial);
                long total = response.Content.Headers.ContentLength ?? update.Size, done = 0, reported = 0;
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (progress != null && total > 0 && done - reported >= total / 100) { reported = done; progress.Report(Math.Min(1.0, done / (double)total)); }
                }
                progress?.Report(1.0);
            }
            await UpdateVerifier.VerifyAsync(partial, update.Sha256, ct);
            File.Move(partial, path, overwrite: true);
        }
        catch (Exception ex)
        {
            try { File.Delete(partial); } catch (IOException) { }
            if (ex is not System.OperationCanceledException) Log?.Invoke($"update: could not download {update.Name}: {ex.Message}");
            throw;
        }
        Log?.Invoke($"update: downloaded {update.Name} ({new FileInfo(path).Length / 1024} KB) and its checksum matched");
        return path;
    }

    /// <summary>False when the person has not yet allowed Yaparr to install packages (Android asks for that once, in its settings).</summary>
    public static bool MayInstall => !OperatingSystem.IsAndroidVersionAtLeast(26) || (Application.Context.PackageManager?.CanRequestPackageInstalls() ?? false);

    /// <summary>Opens Android's page where Yaparr is allowed to install packages.</summary>
    public static void OpenInstallPermission()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var context = Application.Context;
        var intent = new Intent(Settings.ActionManageUnknownAppSources, Android.Net.Uri.Parse("package:" + context.PackageName));
        intent.AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
    }

    /// <summary>
    /// Gives the package to Android's installer. Android shows its own confirmation; when the person accepts, the app is replaced and closes. A failure is
    /// reported through <see cref="InstallFailed"/>.
    /// </summary>
    public static void Install(string path)
    {
        var context = Application.Context;
        var installer = context.PackageManager!.PackageInstaller;
        using var parameters = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
        int id = installer.CreateSession(parameters);
        using (var session = installer.OpenSession(id))
        {
            using (var output = session.OpenWrite("yaparr", 0, new FileInfo(path).Length))
            {
                using var input = File.OpenRead(path);
                var buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0) output!.Write(buffer, 0, read);
                session.Fsync(output!);
            }
            // Android fills in what the receiver needs (the status, and the confirmation to show), so the intent has to be one it may change.
            var intent = new Intent(context, typeof(UpdateInstallReceiver)).SetPackage(context.PackageName);
            var pending = PendingIntent.GetBroadcast(context, id, intent, PendingIntentFlags.UpdateCurrent | (OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable : 0))!;
            session.Commit(pending.IntentSender!);
        }
        Log?.Invoke("update: handed the package to Android's installer");
    }

    internal static void ReportFailure(string text)
    {
        Log?.Invoke("update: the install did not finish: " + text);
        InstallFailed?.Invoke(text);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Yaparr-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}

/// <summary>Hears from Android's installer: it asks for the person's confirmation, and says when an install did not work.</summary>
[BroadcastReceiver(Exported = false)]
public sealed class UpdateInstallReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null || intent == null) return;
        var status = (PackageInstallStatus)intent.GetIntExtra(PackageInstaller.ExtraStatus, (int)PackageInstallStatus.Failure);
        switch (status)
        {
            case PackageInstallStatus.PendingUserAction:
                var confirm = OperatingSystem.IsAndroidVersionAtLeast(33)
                    ? intent.GetParcelableExtra(Intent.ExtraIntent, Java.Lang.Class.FromType(typeof(Intent))) as Intent
#pragma warning disable CA1422
                    : intent.GetParcelableExtra(Intent.ExtraIntent) as Intent;
#pragma warning restore CA1422
                if (confirm == null) break;
                confirm.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(confirm);
                break;
            case PackageInstallStatus.Success:
                break;
            default:
                var message = intent.GetStringExtra(PackageInstaller.ExtraStatusMessage);
                AppUpdater.ReportFailure(status == PackageInstallStatus.FailureAborted ? "Cancelled." : string.IsNullOrWhiteSpace(message) ? status.ToString() : message);
                break;
        }
    }
}
