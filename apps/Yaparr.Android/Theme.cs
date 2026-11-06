using Avalonia.Media;

namespace Yaparr.AndroidApp;

/// <summary>The colours of the app, looked up by name in the current light or dark theme.</summary>
internal static class Theme
{
    public static IBrush Brush(string key) =>
        Avalonia.Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : Brushes.Gray;

    /// <summary>A colour for a name's circle: the same name always gets the same colour.</summary>
    public static IBrush TintFor(string name)
    {
        uint hash = 2166136261;
        foreach (var c in name) hash = (hash ^ c) * 16777619;
        return new SolidColorBrush(new HsvColor(1, hash % 360, 0.55, 0.78).ToRgb());
    }
}
