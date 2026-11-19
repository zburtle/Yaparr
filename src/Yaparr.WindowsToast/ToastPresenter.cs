using CommunityToolkit.WinUI.Notifications;
using Yaparr.AppCore;

namespace Yaparr.WindowsToast;

/// <summary>
/// Toasts that can be answered, made the way a program without a packaged identity makes them (the toolkit registers a handler for it under the user's account, no
/// shortcut is needed): a box to type a reply into, with a Reply button beside it, a Mute room button for a room, and a click on the toast itself to open the chat. What is
/// done comes back through <see cref="Answered"/> while the program runs.
/// </summary>
public sealed class ToastPresenter : IToastPresenter
{
    public event Action<uint, string, string?>? Answered;

    public ToastPresenter() => ToastNotificationManagerCompat.OnActivated += OnActivated;

    public void Show(uint id, string tag, string title, string body, bool canMute)
    {
        var toast = new ToastContentBuilder()
            .AddArgument("id", id.ToString())
            .AddArgument("action", "open")
            .AddText(Short(title, 80))
            .AddText(Short(body, 250))
            .AddAudio(new ToastAudio { Silent = true })   // the app makes its own sounds (and a muted room none)
            .AddInputTextBox("reply", placeHolderContent: "Reply…")
            .AddButton(new ToastButton().SetContent("Reply").AddArgument("action", "reply").SetTextBoxId("reply").SetBackgroundActivation());
        if (canMute) toast.AddButton(new ToastButton().SetContent("Mute room").AddArgument("action", "mute").SetBackgroundActivation());
        toast.Show(t => { t.Tag = Tag(tag); t.Group = "yaparr"; });
    }

    public void Clear()
    {
        try { ToastNotificationManagerCompat.History.Clear(); }
        catch (Exception) { }   // nothing to take out, or the system's notification service is not there
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            var arguments = ToastArguments.Parse(e.Argument);
            if (!arguments.TryGetValue("id", out var text) || !uint.TryParse(text, out var id)) return;
            arguments.TryGetValue("action", out var action);
            string? typed = e.UserInput.TryGetValue("reply", out var value) ? value as string : null;
            // Reply with nothing typed is only a click.
            Answered?.Invoke(id, action ?? "open", string.IsNullOrWhiteSpace(typed) ? null : typed);
        }
        catch (Exception) { }   // an answer that cannot be read is dropped; nothing is sent
    }

    // A toast's tag is at most 64 characters.
    private static string Tag(string tag) => tag.Length <= 64 ? tag : tag[..64];

    private static string Short(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
