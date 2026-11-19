using System.Reflection;

namespace Yaparr.Desktop;

/// <summary>
/// Windows toasts that can be answered (<see cref="IToastPresenter"/>, made by <c>Yaparr.WindowsToast</c>, a Windows-only library that sits next to the program and is
/// loaded when the first pop-up is shown). When it is not there, or Windows will not make a toast, the plain balloon of <see cref="WindowsToast"/> is used as before.
/// </summary>
internal static class WindowsPopups
{
    private static readonly PopupRegistry Popups = new();
    private static IToastPresenter? _presenter;
    private static bool _tried;
    private static uint _next;

    /// <summary>Shows a toast the person can answer. False if there is none to show (another system, the library missing, Windows refusing): use the balloon.</summary>
    public static bool TryShow(string tag, string title, string body, PopupActions actions, Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var presenter = Presenter(log);
        if (presenter == null) return false;
        try
        {
            uint id = Interlocked.Increment(ref _next);
            Popups.Add(id, actions);
            presenter.Show(id, tag, title, body, actions.Mute != null);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log?.Invoke("could not show a toast, using the balloon: " + ex.Message);
            _presenter = null;   // not tried again this run
            return false;
        }
    }

    /// <summary>Takes this program's toasts out of the notification centre (on quitting).</summary>
    public static void Clear() => _presenter?.Clear();

    private static IToastPresenter? Presenter(Action<string>? log)
    {
        if (_tried) return _presenter;
        _tried = true;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Yaparr.WindowsToast.dll");
            if (!File.Exists(path)) return null;
            var type = Assembly.LoadFrom(path).GetType("Yaparr.WindowsToast.ToastPresenter");
            if (type == null || Activator.CreateInstance(type) is not IToastPresenter presenter) return null;
            presenter.Answered += (id, action, text) =>
            {
                // Reply with something typed is a reply; a Reply (or a click) with nothing typed opens the chat to write there.
                if (action == "reply" && text != null) Popups.Reply(id, text);
                else Popups.Invoke(id, action);
            };
            return _presenter = presenter;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log?.Invoke("toasts that can be answered are not available: " + ex.Message);
            return null;
        }
    }
}
