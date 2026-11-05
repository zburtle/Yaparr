using Avalonia;

namespace Yaparr.Desktop;

internal static class Program
{
    /// <summary>The link this copy was started with (the operating system opened a yaparr:// link and no copy was running), if any.</summary>
    public static DeepLink? StartupLink { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        CrashLog.Install();
        if (DeepLink.FromArgs(args) is { } link)
        {
            // A copy that is already running takes the link and this one has nothing more to do.
            var dataIndex = Array.IndexOf(args, "--data");
            var data = dataIndex >= 0 && dataIndex + 1 < args.Length ? args[dataIndex + 1] : AppData.DefaultDirectory;
            if (LinkChannel.TrySend(LinkChannel.NameFor(data), link.ToAppUri())) return;
            StartupLink = link;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
