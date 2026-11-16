using Avalonia.Interactivity;
using Avalonia.Threading;
using Yaparr.AppCore;

namespace Yaparr.AndroidApp;

// Push notifications in the settings: which push app wakes this phone, and turning it on and off (see PushManager).
public partial class MainView
{
    private IReadOnlyList<(string Package, string Label)> _distributors = Array.Empty<(string, string)>();
    private bool _pushWatched;

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
            text = "On, through " + (_distributors.FirstOrDefault(d => d.Package == state.Distributor).Label ?? state.Distributor) + ".";
        else if (PushManager.Status.Length > 0) text = PushManager.Status;
        else if (!signedIn) text = "Off. Sign in to your Yaparr Cloud account to turn it on.";
        else if (_distributors.Count == 0) text = "Off. Install a UnifiedPush app, such as ntfy, then come back here.";
        else text = "Off.";
        if (state.Registered && PushManager.Status.Length > 0) text += " " + PushManager.Status;
        PushState.Text = text;

        PushDistributorBox.IsVisible = !state.Registered && _distributors.Count > 1;
        if (PushDistributorBox.IsVisible)
        {
            PushDistributorBox.ItemsSource = _distributors.Select(d => d.Label).ToList();
            if (PushDistributorBox.SelectedIndex < 0) PushDistributorBox.SelectedIndex = 0;
        }
        PushOnButton.IsVisible = !state.Registered;
        PushOnButton.IsEnabled = signedIn && _distributors.Count > 0;
        PushOffButton.IsVisible = state.Registered || state.Distributor.Length > 0;
    }

    private void PushOnClicked(object? sender, RoutedEventArgs e)
    {
        if (_distributors.Count == 0) { ShowPush(); return; }
        var chosen = _distributors[Math.Clamp(PushDistributorBox.SelectedIndex, 0, _distributors.Count - 1)];
        PushManager.Enable(Android.App.Application.Context, chosen.Package);
    }

    private void PushOffClicked(object? sender, RoutedEventArgs e) => PushManager.Disable(Android.App.Application.Context);
}
