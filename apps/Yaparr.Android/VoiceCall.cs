using Android.Content;
using Android.Media;
using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Voice;

namespace Yaparr.AndroidApp;

/// <summary>
/// One call on the phone: the voice session, the microphone and speakers (<see cref="VoiceAudio"/>), and what Android needs for a call:
/// the communication audio mode, audio focus (so another app's audio gives way, and a phone call is noticed) and the choice of the loudspeaker or
/// the earpiece. Dispose it to leave.
/// </summary>
internal sealed class VoiceCall : IDisposable
{
    private readonly AudioManager? _audio;
    private readonly AudioFocusRequestClass? _focus;
    private readonly VoiceAudio _devices;

    public VoiceSession Session { get; }

    /// <summary>Another app or a phone call took the audio; the microphone is muted until you unmute.</summary>
    public event Action? Interrupted;

    private sealed class FocusListener : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
    {
        private readonly Action _lost;
        public FocusListener(Action lost) => _lost = lost;

        public void OnAudioFocusChange(AudioFocus focusChange)
        {
            if (focusChange is AudioFocus.Loss or AudioFocus.LossTransient) _lost();
        }
    }

    public VoiceCall(Context context, ChatRoom room, string channel)
    {
        Session = room.JoinVoice(channel);
        // What a phone wants in a call: quiet voices raised, and a crowd kept from clipping. Echo and noise are dealt with by Android itself: the
        // microphone and speakers are opened as a call's (see VoiceAudio), which switches on the phone's own echo cancellation and noise suppression.
        Session.Activation = VoiceActivation.VoiceActivity;
        Session.LevelSpeakers = true;
        _audio = context.GetSystemService(Context.AudioService) as AudioManager;
        if (_audio != null)
        {
            _audio.Mode = Mode.InCommunication;
            // A headset or a Bluetooth device a person has connected is what they want; otherwise the loudspeaker, as in a group call.
            var first = AvailableRoutes();
            Select(first.Contains(AudioRoute.Bluetooth) ? AudioRoute.Bluetooth : first.Contains(AudioRoute.Headset) ? AudioRoute.Headset : AudioRoute.Speaker);
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                var attrs = new AudioAttributes.Builder()!.SetUsage(AudioUsageKind.VoiceCommunication)!.SetContentType(AudioContentType.Speech)!.Build()!;
                _focus = new AudioFocusRequestClass.Builder(AudioFocus.Gain)!.SetAudioAttributes(attrs)!
                    .SetOnAudioFocusChangeListener(new FocusListener(OnFocusLost))!.Build();
                _audio.RequestAudioFocus(_focus!);
            }
        }
        try
        {
            _devices = new VoiceAudio(Session, null, null, forCall: true);
        }
        catch
        {
            Release();
            Session.Leave();
            throw;
        }
    }

    private void OnFocusLost()
    {
        Session.Muted = true;
        Interrupted?.Invoke();
    }

    /// <summary>Where the sound of the call goes now.</summary>
    public AudioRoute Route { get; private set; } = AudioRoute.Speaker;

    /// <summary>Where the sound can go now: the phone's earpiece and loudspeaker, and a headset or Bluetooth device when one is connected.</summary>
    public IReadOnlyList<AudioRoute> AvailableRoutes()
    {
        var routes = new List<AudioRoute> { AudioRoute.Phone, AudioRoute.Speaker };
        if (_audio != null && OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            try
            {
                var types = _audio.AvailableCommunicationDevices.Select(d => d.Type).ToList();
                if (types.Any(IsBluetooth)) routes.Add(AudioRoute.Bluetooth);
                if (types.Any(IsHeadset)) routes.Add(AudioRoute.Headset);
            }
            catch (Java.Lang.RuntimeException) { }
        }
        return routes;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android31.0")]
    private static bool IsBluetooth(AudioDeviceType t) => t is AudioDeviceType.BluetoothSco or AudioDeviceType.BleHeadset;

    [System.Runtime.Versioning.SupportedOSPlatform("android31.0")]
    private static bool IsHeadset(AudioDeviceType t) => t is AudioDeviceType.WiredHeadset or AudioDeviceType.WiredHeadphones or AudioDeviceType.UsbHeadset;

    /// <summary>Sends the sound of the call to <paramref name="route"/>.</summary>
    public void Select(AudioRoute route)
    {
        Route = route;
        if (_audio == null) return;
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(31)) SelectModern(_audio, route);
            else
            {
#pragma warning disable CA1422
                _audio.SpeakerphoneOn = route == AudioRoute.Speaker;
#pragma warning restore CA1422
            }
        }
        catch (Java.Lang.RuntimeException) { }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android31.0")]
    private static void SelectModern(AudioManager audio, AudioRoute route)
    {
        var devices = audio.AvailableCommunicationDevices;
        var wanted = route switch
        {
            AudioRoute.Speaker => devices.FirstOrDefault(d => d.Type == AudioDeviceType.BuiltinSpeaker),
            AudioRoute.Bluetooth => devices.FirstOrDefault(d => IsBluetooth(d.Type)),
            AudioRoute.Headset => devices.FirstOrDefault(d => IsHeadset(d.Type)),
            _ => devices.FirstOrDefault(d => d.Type == AudioDeviceType.BuiltinEarpiece),
        };
        if (wanted != null) audio.SetCommunicationDevice(wanted);
    }

    private void Release()
    {
        if (_audio == null) return;
        try
        {
            if (_focus != null && OperatingSystem.IsAndroidVersionAtLeast(26)) _audio.AbandonAudioFocusRequest(_focus);
            if (OperatingSystem.IsAndroidVersionAtLeast(31)) _audio.ClearCommunicationDevice();
#pragma warning disable CA1422
            else _audio.SpeakerphoneOn = false;
#pragma warning restore CA1422
            _audio.Mode = Mode.Normal;
        }
        catch (Java.Lang.RuntimeException) { }
    }

    public void Dispose()
    {
        _devices.Dispose();
        Session.Leave();
        Release();
    }
}

/// <summary>Where the sound of a call can go, as Android's own call screen names them.</summary>
public enum AudioRoute
{
    /// <summary>The earpiece, held to the ear.</summary>
    Phone,
    Speaker,
    Bluetooth,
    /// <summary>A wired or USB headset or headphones.</summary>
    Headset,
}

public static class AudioRoutes
{
    public static string Label(AudioRoute route) => route switch
    {
        AudioRoute.Phone => "Phone",
        AudioRoute.Speaker => "Speaker",
        AudioRoute.Bluetooth => "Bluetooth",
        _ => "Headset",
    };
}
