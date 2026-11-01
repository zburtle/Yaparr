using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yaparr.Screen;

namespace Yaparr.Desktop;

/// <summary>Shows a download with a progress bar and a Cancel button, and closes with the path of what was fetched (or null if it was cancelled or failed).</summary>
public partial class DownloadDialog : Window
{
    private readonly CancellationTokenSource _cts = new();

    public DownloadDialog() => InitializeComponent();

    public string? Error { get; private set; }

    public static Task<(string? Path, string? Error)> RunAsync(Window owner, FfmpegPackage package) =>
        RunAsync(owner, "Getting FFmpeg", $"Downloading FFmpeg ({package.SizeBytes / 1_048_576} MB), once. It does the screen capture and the video decoding.",
            (progress, ct) => FfmpegInstaller.InstallAsync(package, progress, ct: ct));

    /// <param name="work">The download: it reports progress from 0 to 1 and returns the path of what it fetched.</param>
    public static async Task<(string? Path, string? Error)> RunAsync(Window owner, string title, string message, Func<IProgress<double>, CancellationToken, Task<string>> work)
    {
        var dialog = new DownloadDialog { Title = title };
        dialog.Opened += async (_, _) => await dialog.DownloadAsync(message, work);
        var path = await dialog.ShowDialog<string?>(owner);
        return (path, dialog.Error);
    }

    private async Task DownloadAsync(string message, Func<IProgress<double>, CancellationToken, Task<string>> work)
    {
        StatusText.Text = message;
        try
        {
            var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() => Bar.Value = p * 100));
            var path = await work(progress, _cts.Token);
            Close(path);
        }
        catch (OperationCanceledException) { Close(null); }
        catch (Exception ex)
        {
            Error = ex.Message;
            Close(null);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        _cts.Cancel();
    }
}
