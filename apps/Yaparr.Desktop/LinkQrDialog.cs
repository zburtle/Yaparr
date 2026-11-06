using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Yaparr.Desktop;

/// <summary>
/// Shows the QR code that links a phone, for two minutes. It holds the account key and the passphrase, so it is not kept on
/// screen: it closes by itself, and the text says to keep the screen private while it shows.
/// </summary>
internal static class LinkQrDialog
{
    private const int Seconds = 120;

    public static async Task ShowAsync(Window owner, string payload)
    {
        bool[,] matrix;
        try { matrix = AppCore.LinkQr.Matrix(payload); }
        catch (ArgumentException)
        {
            await Dialogs.MessageAsync(owner, "Link another device", "That is too much to fit in a QR code. Use a file or the clipboard instead.");
            return;
        }

        // Four pixels to a square: smaller ones are hard for a phone camera to tell apart.
        double side = Math.Max(420, matrix.GetLength(0) * 4);
        var remaining = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.7 };
        var done = new Button { Content = "Done", MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center };
        done.Classes.Add("primary");
        var dialog = new Window
        {
            Title = "Link another device",
            Width = side + 32,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "On your phone, open Yaparr, choose \"Link with a desktop\" and scan this code. " +
                               "Anyone who scans it can act as you, so keep this screen to yourself while it shows.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    new QrView { Matrix = matrix, Width = side, Height = side, HorizontalAlignment = HorizontalAlignment.Center },
                    remaining,
                    done,
                },
            },
        };
        done.Click += (_, _) => dialog.Close();

        int left = Seconds;
        void Show() => remaining.Text = $"This closes in {left / 60}:{left % 60:00}.";
        Show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            if (--left <= 0) { dialog.Close(); return; }
            Show();
        };
        timer.Start();
        dialog.Closed += (_, _) => timer.Stop();
        await dialog.ShowDialog(owner);
    }
}
