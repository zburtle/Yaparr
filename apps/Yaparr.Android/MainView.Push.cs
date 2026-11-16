using Avalonia.Interactivity;
using Avalonia.Threading;
using Yaparr.AppCore;

namespace Yaparr.AndroidApp;

// Push notifications in the settings: which push app wakes this phone, and turning it on and off (see PushManager).
public partial class MainView
{
    private IReadOnlyList<(string Package, string Label)> _distributors = Array.Empty<(string, string)>();
    private bool _pushWatched;
    private const string FirebaseLabel = "Google (Firebase), if you have no push app";

    // The push apps on the phone, then Firebase as the last choice: it is Google's, so it is offered after the ones that are not.
    private List<string> PushChoices() => _distributors.Select(d => d.Label).Append(FirebaseLabel).ToList();

    private void ShowPush()
    {
        if (!_pushWatched)
        {
            _pushWatched = true;
            PushManager.Changed += () => Dispatcher.UIThread.Post(ShowPush);
        }
        var state = PushManager.State;
        _distributors = UnifiedPush.Distributors(Android.App.Application.Context);
        bool signedIn = PushManager.TryServer(out _, out _);

        string text;
        if (state.Registered)
            text = "On, through " + (state.Route == PushRoute.Fcm ? "Firebase" : _distributors.FirstOrDefault(d => d.Package == state.Distributor).Label ?? state.Distributor) + ".";
        else if (PushManager.Status.Length > 0) text = PushManager.Status;
        else if (!signedIn) text = "Off. Sign in to your Yaparr Cloud account to turn it on.";
        else if (_distributors.Count == 0) text = "Off. A UnifiedPush app such as ntfy keeps Google out of it; without one, Firebase is used.";
        else text = "Off.";
        if (state.Registered && PushManager.Status.Length > 0) text += " " + PushManager.Status;
        PushState.Text = text;

        PushDistributorBox.IsVisible = !state.Registered && _distributors.Count > 0;
        if (PushDistributorBox.IsVisible)
        {
            PushDistributorBox.ItemsSource = PushChoices();
            if (PushDistributorBox.SelectedIndex < 0) PushDistributorBox.SelectedIndex = 0;
        }
        PushOnButton.IsVisible = !state.Registered;
        PushOnButton.IsEnabled = signedIn;
        PushOffButton.IsVisible = state.Registered || state.Distributor.Length > 0 || state.Route != PushRoute.None;
    }

    private void PushOnClicked(object? sender, RoutedEventArgs e)
    {
        // With no push app on the phone there is nothing to choose: Firebase.
        int index = _distributors.Count == 0 ? 0 : Math.Clamp(PushDistributorBox.SelectedIndex, 0, _distributors.Count);
        if (index >= _distributors.Count) PushManager.EnableFirebase(Android.App.Application.Context);
        else PushManager.Enable(Android.App.Application.Context, _distributors[index].Package);
    }

    private void PushOffClicked(object? sender, RoutedEventArgs e) => PushManager.Disable(Android.App.Application.Context);
}
