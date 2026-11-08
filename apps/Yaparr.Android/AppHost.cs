using Android.Content;
using Yaparr.AppCore;
using Yaparr.Chat;

namespace Yaparr.AndroidApp;

/// <summary>
/// What lives as long as the app's process, not as long as one screen: where the account is kept, the settings, and the one <see cref="Session"/> (the
/// connection to other people). The screen, the background service and a notification all use the same one, so closing the screen does not end the
/// connection and a screen opened again picks it up instead of making a second one.
/// </summary>
internal static class AppHost
{
    private static AppSettings? s_settings;
    private static Session? s_session;
    private static Task? s_starting;

    public static AppData Data { get; } = new(AppData.DefaultDirectory);

    /// <summary>The settings, read once; the screen and the session share this object, so a change made in one is seen by the other.</summary>
    public static AppSettings Settings => s_settings ??= Data.LoadSettings();

    /// <summary>The rooms of the account that this phone was linked to, from the link, until the session has taken them.</summary>
    public static IReadOnlyList<AccountEntry> LinkedEntries { get; set; } = Array.Empty<AccountEntry>();

    /// <summary>The screen is on show (the activity is resumed): nothing is notified, the unread marks say it.</summary>
    public static bool InForeground { get; set; }

    /// <summary>The activity exists (it is on show, or in the recent apps and not yet destroyed).</summary>
    public static bool ScreenOpen { get; set; }

    /// <summary>The session, or null before there is one.</summary>
    public static Session? Session => s_session;

    /// <summary>The one session of this process, made if it was not. Not started: see <see cref="StartAsync"/>.</summary>
    public static Session GetOrCreateSession()
    {
        if (s_session is { } existing) return existing;
        var session = s_session = new Session(Data, Settings);
        // A call keeps the service on (and a call that ends may end it).
        session.CallChanged += () => { if (OperatingSystem.IsAndroidVersionAtLeast(26)) ConnectionService.Refresh(Android.App.Application.Context); };
        return session;
    }

    /// <summary>
    /// Starts the session once, for whoever asks first (the screen, or the service when the system started the app without one). Asking again waits for the
    /// same start. Does nothing when there is no account yet.
    /// </summary>
    public static Task StartAsync(string name)
    {
        var session = GetOrCreateSession();
        lock (typeof(AppHost))
        {
            if (s_starting is { IsFaulted: true } or { IsCanceled: true }) s_starting = null;
            return s_starting ??= session.StartAsync(name, LinkedEntries);
        }
    }

    /// <summary>Whether this phone has an account with a name, so the session can start without anyone typing anything.</summary>
    public static bool CanStartWithoutScreen => Data.HasIdentity && !string.IsNullOrWhiteSpace(Settings.DisplayName);

    /// <summary>Whether the connection is wanted when the screen is closed: the person asked for it, or a call is going on.</summary>
    public static bool KeepRunning => Settings.BackgroundConnection || s_session?.VoiceChat != null;

    /// <summary>Ends the session (and its connections). The next <see cref="GetOrCreateSession"/> makes a new one.</summary>
    public static void Shutdown()
    {
        var session = s_session;
        s_session = null;
        s_starting = null;
        try { session?.Dispose(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Android.Util.Log.Warn("Yaparr", "ending the session: " + ex.Message); }
    }

    /// <summary>Makes the service match what is wanted: on while <see cref="KeepRunning"/>, off otherwise.</summary>
    public static void UpdateService(Context context)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) ConnectionService.Refresh(context);
    }
}
