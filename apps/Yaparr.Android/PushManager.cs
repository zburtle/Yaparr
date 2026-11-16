using Android.Content;
using Yaparr.AppCore;

namespace Yaparr.AndroidApp;

/// <summary>
/// Push notifications on this phone: turning them on (asking a UnifiedPush distributor for an address, then registering it with the person's Yaparr Cloud server,
/// which gives back a wake address), turning them off, and what to do when a push arrives. The wake address goes to the people in the person's rooms (see
/// <see cref="Yaparr.Chat.ChatNode.WakeAddressOfMine"/>); when one of them writes and this phone is not connected, they ask the server, the server pushes with
/// nothing in the push, and <see cref="OnWake"/> brings the connection up so the message arrives and notifies like any other.
/// </summary>
internal static class PushManager
{
    /// <summary>How long the connection is kept after a push: long enough to connect and be sent what was missed (see <see cref="AppHost.KeepRunning"/>).</summary>
    public static readonly TimeSpan WakeWindow = TimeSpan.FromMinutes(2);

    private static readonly object Gate = new();

    /// <summary>What the settings page shows, kept for the page: the last thing that went wrong or what it is waiting for.</summary>
    public static string Status { get; private set; } = "";

    /// <summary>Raised on another thread when the registration, or what is said about it, changes.</summary>
    public static event Action? Changed;

    private static void Say(string status)
    {
        Status = status;
        Changed?.Invoke();
    }

    public static PushState State => AppHost.Data.LoadPush();

    /// <summary>A broadcast from a push app is believed only if it carries the token this app made for the registration (anything on the phone can send one).</summary>
    public static bool IsOurToken(string? token)
    {
        var state = State;
        return !string.IsNullOrEmpty(token) && state.DeviceId.Length > 0 && string.Equals(token, state.DeviceId, StringComparison.Ordinal);
    }

    public static string DistributorPackage() => State.Distributor;

    /// <summary>Whether this phone is signed in to a Yaparr Cloud server, which push registration needs; and which.</summary>
    public static bool TryServer(out Uri server, out string token)
    {
        var backup = AppHost.Data.LoadBackupServer();
        server = BackupServerClient.ParseServerAddress(backup.Url)!;
        token = backup.Token;
        return backup.SignedIn && server != null;
    }

    /// <summary>
    /// Turns push on with a distributor: asks it for an address; the answer arrives in <see cref="OnNewEndpoint"/>, which registers it. The device id is the token the
    /// distributor must quote, so it is made here, once.
    /// </summary>
    public static void Enable(Context context, string distributor)
    {
        if (!TryServer(out _, out _)) { Say("Sign in to your Yaparr Cloud account first."); return; }
        PushState state;
        lock (Gate)
        {
            var old = State;
            state = new PushState { DeviceId = old.DeviceId.Length > 0 ? old.DeviceId : Guid.NewGuid().ToString("N"), Route = PushRoute.None, Distributor = distributor, Server = old.Server, WakeAddress = old.WakeAddress };
            AppHost.Data.SavePush(state);
        }
        Say("Waiting for the push app…");
        UnifiedPush.Register(context, distributor, state.DeviceId);
    }

    /// <summary>The distributor gave an address: it is registered with the server and the wake address it gives back is told to the rooms.</summary>
    public static void OnNewEndpoint(Context context, string? endpoint)
    {
        if (string.IsNullOrEmpty(endpoint)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (!TryServer(out var server, out var token)) { Say("Sign in to your Yaparr Cloud account first."); return; }
                var current = State;
                using var client = new PushClient(server, token);
                var options = await client.OptionsAsync();
                if (!options.UnifiedPush) { Say("This server does not send push notifications."); return; }
                var state = await PushSetup.EnsureAsync(client, server, current, PushRoute.UnifiedPush, endpoint, current.Distributor);
                AppHost.Data.SavePush(state);
                AppHost.Session?.SetWakeAddress(state.WakeAddress);
                Say("");
            }
            catch (BackupServerException ex) { Say(ex.SignedOut ? "Sign in to your Yaparr Cloud account again." : ex.Message); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { Say("Push could not be turned on: " + ex.Message); }
        });
    }

    /// <summary>The push app dropped the registration (it was removed, or the person signed out of its server): push is off until the person chooses again.</summary>
    public static void OnLost(Context context, string why)
    {
        lock (Gate)
        {
            var state = State;
            if (state.DeviceId.Length > 0) AppHost.Data.SavePush(new PushState { DeviceId = state.DeviceId });
        }
        AppHost.Session?.SetWakeAddress(null);
        Say(why);
    }

    /// <summary>Turns push off: the distributor, the server and the rooms are told, and the wake address stops working.</summary>
    public static void Disable(Context context)
    {
        var state = State;
        if (state.Distributor.Length > 0 && state.DeviceId.Length > 0) UnifiedPush.Unregister(context, state.Distributor, state.DeviceId);
        AppHost.Data.SavePush(new PushState { DeviceId = state.DeviceId });
        AppHost.Session?.SetWakeAddress(null);
        Say("");
        _ = Task.Run(async () =>
        {
            try
            {
                PushClient? client = TryServer(out var server, out var token) ? new PushClient(server, token) : null;
                using (client) await PushSetup.TurnOffAsync(client, state);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or BackupServerException) { }
        });
    }

    /// <summary>A push arrived: the connection is brought up for a while so the people who wrote can send what they have, and it notifies as messages come in.</summary>
    public static void OnWake(Context context)
    {
        AppHost.WakeUntil = Environment.TickCount64 + (long)WakeWindow.TotalMilliseconds;
        bool started = OperatingSystem.IsAndroidVersionAtLeast(26) && AppHost.CanStartWithoutScreen && ConnectionService.StartForWake(context);
        if (!started && !AppHost.ScreenOpen && OperatingSystem.IsAndroidVersionAtLeast(26)) Notifier.Wake(context);
        // After the window the service stops again, unless the person keeps the connection on.
        _ = Task.Delay(WakeWindow + TimeSpan.FromSeconds(2)).ContinueWith(_ => AppHost.UpdateService(Android.App.Application.Context));
    }
}
