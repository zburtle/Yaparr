using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Yaparr.AppCore;
using Yaparr.Chat;

namespace Yaparr.Desktop;

// The card under a link: made here when a message is sent, shown for every message that carries one.
public partial class MainWindow
{
    private LinkPreviewFetcher? _previewFetcher;

    // How long sending waits for a page. The message is signed with its card, so the card has to be ready before it goes.
    private static readonly TimeSpan PreviewWait = TimeSpan.FromSeconds(4);

    private void SetUpLinkPreviews()
    {
        LinkPreviewItem.IsChecked = _settings.LinkPreviews;
        LinkPreviewItem.PropertyChanged += (_, e) =>
        {
            if (e.Property != Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty) return;
            _settings.LinkPreviews = LinkPreviewItem.IsChecked == true;
            _data.SaveSettings(_settings);
        };
    }

    /// <summary>The card for the first web link in <paramref name="text"/>, or null: no link, previews are off, or the page could not be read in time.</summary>
    private async Task<LinkPreview?> PreviewForAsync(string text)
    {
        if (!_settings.LinkPreviews || LinkPreview.FirstLink(text) is not { } link) return null;
        StatusText.Text = "Getting a preview of the link…";
        try
        {
            _previewFetcher ??= new LinkPreviewFetcher { Log = _log.Write };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(PreviewWait);
            return await _previewFetcher.FetchAsync(link, cts.Token);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _log.Write("could not get a link preview: " + ex.Message);
            return null;
        }
        finally { StatusText.Text = ""; }
    }

    // Opening a link is a choice: the card is the sender's description, so the address it really goes to is shown first.
    private async void PreviewCard_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not MessageVm { Card: { } card } || e.InitialPressMouseButton != MouseButton.Left) return;
        e.Handled = true;
        if (!Uri.TryCreate(card.Url, UriKind.Absolute, out var uri)) return;
        if (!await Dialogs.ConfirmAsync(this, "Open this link?", card.Url + "\n\nIt opens in your browser, and the site will see your address.", "Open", "Cancel")) return;
        try
        {
            if (!await Launcher.LaunchUriAsync(uri))
                await Dialogs.MessageAsync(this, "Open link", "Open " + card.Url + " in a browser.");
        }
        catch (Exception ex) { StatusText.Text = "Couldn't open the link: " + ex.Message; }
    }
}
