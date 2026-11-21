using Yaparr.Chat;

namespace Yaparr.Desktop;

// Explains in plain words how Yaparr finds people and what to do when it cannot. Shown once on the first start, and from Help.
public partial class MainWindow
{
    // Opens a guide (see Guides) in the browser.
    private async Task OpenGuideAsync(string topic)
    {
        if (Yaparr.AppCore.Guides.Url(topic) is not { } guide) return;
        try { await Launcher.LaunchUriAsync(guide); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { StatusText.Text = "Couldn't open your browser: " + ex.Message; }
    }

    private async Task ShowConnectionGuideAsync()
    {
        if (!_settings.ConnectionGuideShown)
        {
            _settings.ConnectionGuideShown = true;
            _data.SaveSettings(_settings);
        }
        var next = await Dialogs.ChoiceAsync(this, "How Yaparr connects you",
            "Yaparr has no company server. Your computer connects straight to the people in your rooms, and most of the time that just works.\n\n" +
            "If people cannot reach you, a VPN or a TURN relay can help. The connection check in Settings, under Network, tells you why.",
            "Open Network settings", "Learn more", "Close");
        if (next == "Open Network settings") ShowSettings("network");
        else if (next == "Learn more") await OpenGuideAsync("networks");
    }
}
