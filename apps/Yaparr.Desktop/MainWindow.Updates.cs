using Avalonia.Controls.ApplicationLifetimes;

namespace Yaparr.Desktop;

// A new version can be installed while Yaparr is running: from a .deb or the MSI by hand, or in a Flatpak by "flatpak update" or the software centre, which
// never close a running app. The running copy then carries on as the old version until it is started again, and starting it again only brings the old
// window to the front. So the app notices (a newer copy was started, or the Flatpak portal says a new version is installed) and offers to restart.
public partial class MainWindow
{
    private bool _offeredRestart;
    private FlatpakUpdates? _flatpakUpdates;

    // Called once the account is known.
    private void StartUpdateWatch()
    {
        if (Program.UpdateFailed) StatusText.Text = "The update was not installed. Yaparr is still the older version.";
        if (!Updater.IsFlatpak) return;
        _ = Task.Run(async () =>
        {
            _flatpakUpdates = await FlatpakUpdates.StartAsync(info =>
            {
                if (info.InstalledAlready) UI(() => _ = OfferRestartAsync("A new version of Yaparr was installed while it was open."));
            }, _cts.Token).ConfigureAwait(false);
            if (_flatpakUpdates == null) _log.Write("flatpak: could not watch for updates (no portal)");
        });
    }

    // Asked at most once a run, and left alone while the person says "Later".
    private async Task OfferRestartAsync(string what)
    {
        if (_offeredRestart) return;
        _offeredRestart = true;
        var inVoice = _voice != null ? "\n\nThe call you are in will end." : "";
        if (!await Dialogs.ConfirmAsync(this, "Restart Yaparr?", $"{what}\n\nRestart now to use the new version?{inVoice}", "Restart now", "Later")) return;
        await RestartIntoNewVersionAsync();
    }

    // Starts the newest copy and closes this one. In a Flatpak this sandbox still holds the old files, so the portal starts the latest version in a new
    // one; if it will not, the app closes and the person opens it again.
    private async Task RestartIntoNewVersionAsync()
    {
        if (Updater.IsFlatpak)
        {
            var started = await FlatpakUpdates.StartLatestAsync("/app/bin/yaparr", new Dictionary<string, string> { [Program.RestartVariable] = "1" });
            if (!started)
            {
                await Dialogs.MessageAsync(this, "Restart Yaparr", "Yaparr will close now. Open it again from your menu to use the new version.");
            }
            _restarting = true;
            _quitting = true;
            (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            return;
        }
        _quitting = true;
        RestartApp();
    }
}
