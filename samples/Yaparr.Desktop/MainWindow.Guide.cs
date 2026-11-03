using Yaparr.Chat;

namespace Yaparr.Desktop;

// Explains in plain words how Yaparr finds people and what to do when it cannot. Shown once on the first start, and from Help.
public partial class MainWindow
{
    private async Task ShowConnectionGuideAsync()
    {
        if (!_settings.ConnectionGuideShown)
        {
            _settings.ConnectionGuideShown = true;
            _data.SaveSettings(_settings);
        }
        var next = await Dialogs.ChoiceAsync(this, "How Yaparr connects you",
            "Yaparr has no company server. Your computer connects straight to the people in your rooms, and most of the time that just works.\n\n" +
            "Some home and work networks make that hard, usually because the router is strict. If people cannot reach you, there are two ways to help:\n\n" +
            "VPN: your Yaparr traffic goes through a WireGuard VPN you already pay for. Other programs are not affected, and people can reach you through the VPN's address.\n\n" +
            "TURN relay: a server that passes Yaparr's traffic along when a direct connection fails. Yaparr has none of its own, so you run one or import one a friend shares. Messages stay signed and encrypted between you and the other person; the relay only forwards them.\n\n" +
            "You can skip all of this. If something does not connect, the connection check in Settings, under Network, tells you why.",
            "Open Network settings", "Close");
        if (next == "Open Network settings") ShowSettings("network");
    }
}
