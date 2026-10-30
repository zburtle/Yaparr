using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yaparr.Screen;

namespace Yaparr.Desktop;

/// <summary>Shows FFmpeg being downloaded, and closes with the path of the program (or null if it was cancelled or failed).</summary>
public partial class DownloadDialog : Window
{
    private readonly CancellationTokenSource _cts = new();

    public DownloadDialog() => InitializeComponent();

    public string? Error { get; private set; }

    public static async Task<(string? Path, string? Error)> RunAsync(Window owner, FfmpegPackage package)
    {
        var dialog = new DownloadDialog();
        dialog.Opened += async (_, _) => await dialog.DownloadAsync(package);
        var path = await dialog.ShowDialog<string?>(owner);
        return (path, dialog.Error);
    }

    private async Task DownloadAsync(FfmpegPackage package)
    {
        StatusText.Text = $"Downloading FFmpeg ({package.SizeBytes / 1_048_576} MB), once. It does the screen capture and the video decoding.";
        try
        {
            var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() => Bar.Value = p * 100));
            var path = await FfmpegInstaller.InstallAsync(package, progress, ct: _cts.Token);
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
