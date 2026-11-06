using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Yaparr.AndroidApp;

public partial class App : Avalonia.Application
{
    /// <summary>Reads a QR code with the phone's camera. Set by the activity; null where there is nothing to scan with.</summary>
    public static QrScanner? Scanner { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IActivityApplicationLifetime activity)
            activity.MainViewFactory = () => new MainView();
        base.OnFrameworkInitializationCompleted();
    }
}
