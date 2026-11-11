using Avalonia.Threading;
using Yaparr.AppCore;

namespace Yaparr.AndroidApp;

/// <summary>
/// The Yaparr links the phone has opened in the app (<c>yaparr://join/…</c>, <c>yaparr://chat/…</c> and the <c>https://yaparr.com</c> forms), waiting for the
/// screen to deal with them. A link never acts on its own: the person is asked first, because joining a room or starting a chat lets the
/// other side see their IP address. Links that arrive before the app is ready wait, a few at most.
/// </summary>
internal static class LinkInbox
{
    private const int MaxWaiting = 4;
    private static readonly Queue<DeepLink> s_waiting = new();

    /// <summary>A link has arrived, on the UI thread.</summary>
    public static event Action? Arrived;

    /// <summary>Takes in what an intent carried: anything that is not a well-formed Yaparr link is dropped.</summary>
    public static void Offer(string? uri)
    {
        if (DeepLink.Parse(uri) is not { } link) return;
        // The Android app has no backup server yet, so a link that signs in to one has nothing to do here.
        if (link.Kind == DeepLinkKind.BackupSignIn) return;
        lock (s_waiting)
        {
            if (s_waiting.Count < MaxWaiting) s_waiting.Enqueue(link);
        }
        Dispatcher.UIThread.Post(() => Arrived?.Invoke());
    }

    public static DeepLink? Take()
    {
        lock (s_waiting) return s_waiting.Count > 0 ? s_waiting.Dequeue() : null;
    }
}
