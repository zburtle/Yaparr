using Avalonia;

namespace Yaparr.Desktop;

internal static class Program
{
    /// <summary>The link this copy was started with (the operating system opened a yaparr:// link and no copy was running), if any.</summary>
    public static DeepLink? StartupLink { get; private set; }

    /// <summary>Set by a copy that starts its successor, which then does not hand over to it.</summary>
    public const string RestartVariable = "YAPARR_RESTARTED";

    [STAThread]
    public static void Main(string[] args)
    {
        CrashLog.Install();
        // A copy that is already running (possibly hidden in the notification area) takes the link, or is just brought to the front, and this one has
        // nothing more to do. A copy started by the other one's restart does not look: the old one is still closing.
        var link = DeepLink.FromArgs(args);
        var restarted = Environment.GetEnvironmentVariable(RestartVariable) == "1";
        Environment.SetEnvironmentVariable(RestartVariable, null);
        if (!restarted)
        {
            var dataIndex = Array.IndexOf(args, "--data");
            var data = dataIndex >= 0 && dataIndex + 1 < args.Length ? args[dataIndex + 1] : AppData.DefaultDirectory;
            if (LinkChannel.TrySend(LinkChannel.NameFor(data), link?.ToAppUri() ?? LinkChannel.ShowLine, link != null ? 1000 : 250)) return;
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
