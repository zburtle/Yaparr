namespace Yaparr.AppCore;

/// <summary>
/// Shows Windows toasts that can be answered (a box to type a reply into, and buttons), made by <c>Yaparr.WindowsToast</c>, which needs a Windows-only target and so is
/// loaded by the desktop app while it runs rather than referenced. What the person does comes back through <see cref="Answered"/>, on any thread, with the number the
/// pop-up was shown with.
/// </summary>
public interface IToastPresenter
{
    /// <summary>Shows a toast. A later one with the same <paramref name="tag"/> (a chat) takes the earlier one's place, so a busy chat does not pile them up.</summary>
    void Show(uint id, string tag, string title, string body, bool canMute);

    /// <summary>The person used a toast: <c>open</c> (clicked it), <c>reply</c> (with what they typed, or null when the box was empty) or <c>mute</c>.</summary>
    event Action<uint, string, string?>? Answered;

    /// <summary>Takes this app's toasts out of the notification centre (when it quits, so that a toast left there does not start it again just to find nothing).</summary>
    void Clear();
}
