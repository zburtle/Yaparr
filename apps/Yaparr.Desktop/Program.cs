using Avalonia;

namespace Yaparr.Desktop;

internal static class Program
{
    /// <summary>The link this copy was started with (the operating system opened a yaparr:// link and no copy was running), if any.</summary>
    public static DeepLink? StartupLink { get; private set; }

    /// <summary>Set by a copy that starts its successor, which then does not hand over to it.</summary>
    public const string RestartVariable = "YAPARR_RESTARTED";

    /// <summary>Set for the copy started after an update that could not be installed (on Linux the update is installed once the app has closed, and the app starts again either way).</summary>
    public const string UpdateFailedVariable = "YAPARR_UPDATE_FAILED";

    /// <summary>True when this copy was started after an update that was not installed.</summary>
    public static bool UpdateFailed { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        CrashLog.Install();
        // A copy that is already running (possibly hidden in the notification area) takes the link, or is just brought to the front, and this one has
        // nothing more to do. A copy started by the other one's restart does not look: the old one is still closing.
        var link = DeepLink.FromArgs(args);
        var restarted = Environment.GetEnvironmentVariable(RestartVariable) == "1";
        Environment.SetEnvironmentVariable(RestartVariable, null);
        UpdateFailed = Environment.GetEnvironmentVariable(UpdateFailedVariable) == "1";
        Environment.SetEnvironmentVariable(UpdateFailedVariable, null);
        if (!restarted)
        {
            var dataIndex = Array.IndexOf(args, "--data");
            var data = dataIndex >= 0 && dataIndex + 1 < args.Length ? args[dataIndex + 1] : AppData.DefaultDirectory;
            // The running copy is told which version this one is: if it is older (an update was installed while it ran), it offers to restart.
            var version = LinkChannel.VersionPrefix + Updater.CurrentVersion.ToString(3);
            if (LinkChannel.TrySend(LinkChannel.NameFor(data), link?.ToAppUri() ?? LinkChannel.ShowLine, link != null ? 1000 : 250, version)) return;
        }
        StartupLink = link;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
