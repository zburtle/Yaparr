using Avalonia.Interactivity;
using Avalonia.Threading;
using Yaparr.Updates;

namespace Yaparr.AndroidApp;

// Updating the app from its own releases: a look when the app opens (which can be turned off), and a button in the settings. AppUpdater does the work;
// Android always asks for a confirmation before it replaces the app.
public partial class MainView
{
    private AndroidUpdate? _update;
    private bool _updating;

    // A few seconds after the app opens, so that it never slows the start, and once for each version: a newer one is offered, and what is declined is
    // not offered again until there is another.
    private async Task CheckForUpdateAtStartAsync()
    {
        if (!_settings.CheckForUpdates || AppUpdater.IsTestBuild) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            if (await AppUpdater.CheckAsync() is not { } update) return;
            _update = update;
            var version = update.Version.ToString();
            if (_settings.OfferedUpdate == version || ConfirmPanel.IsVisible) return;
            _settings.OfferedUpdate = version;
            _data.SaveSettings(_settings);
            Ask($"Yaparr {version} is available.\n\nIt is downloaded from GitHub and checked before it is installed, and Android asks you to confirm.", "Update", () =>
            {
                // The progress is shown in the settings.
                SettingsClicked(null, new RoutedEventArgs());
                return InstallUpdateAsync(update);
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* No network, or GitHub is not answering: nothing to offer. */ }
    }

    private void OnUpdateInstallFailed(string text) => Dispatcher.UIThread.Post(() => UpdateText.Text = "The update did not install: " + text);

    // What the settings say about updates, and what the button does.
    private void ShowUpdateState()
    {
        UpdateCheck.IsVisible = !AppUpdater.IsTestBuild;
        if (AppUpdater.IsTestBuild)
        {
            UpdateText.Text = "This is a test build, which a release cannot replace. It is updated by installing the next test build.";
            UpdateButton.IsVisible = false;
            return;
        }
        UpdateButton.IsVisible = true;
        if (_updating) return;
        UpdateText.Text = _update != null ? $"Version {_update.Version} is available." : "";
        UpdateButton.Content = _update != null ? $"Update to {_update.Version}" : "Check for updates";
    }

    private async void UpdateClicked(object? sender, RoutedEventArgs e)
    {
        if (_updating) return;
        if (_update != null) { await InstallUpdateAsync(_update); return; }
        _updating = true;
        UpdateButton.IsEnabled = false;
        UpdateText.Text = "Looking for updates...";
        try
        {
            _update = await AppUpdater.CheckAsync();
            _updating = false;
            ShowUpdateState();
            if (_update == null) UpdateText.Text = "Yaparr is up to date.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _updating = false;
            UpdateText.Text = "Could not look for updates: " + ex.Message;
        }
        UpdateButton.IsEnabled = true;
    }

    private async Task InstallUpdateAsync(AndroidUpdate update)
    {
        if (_updating) return;
        // Android has to be told once that Yaparr may install packages; the person comes back and taps again.
        if (!AppUpdater.MayInstall)
        {
            UpdateText.Text = "Allow Yaparr to install updates in the page that opens, then come back and tap the button again.";
            AppUpdater.OpenInstallPermission();
            return;
        }
        _updating = true;
        UpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => UpdateText.Text = $"Downloading {update.Version}... {p:P0}");
            UpdateText.Text = $"Downloading {update.Version}...";
            var path = await AppUpdater.DownloadAsync(update, progress);
            UpdateText.Text = "Checked. Confirm the update in the window Android shows.";
            AppUpdater.Install(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UpdateText.Text = "Could not update: " + ex.Message;
        }
        finally
        {
            _updating = false;
            UpdateButton.IsEnabled = true;
        }
    }

    private void UpdateCheckChanged(object? sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.CheckForUpdates = UpdateCheck.IsChecked == true;
        _data.SaveSettings(_settings);
    }
}
