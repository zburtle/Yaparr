using Avalonia.Layout;
using Avalonia.Media;

namespace Yaparr.AndroidApp;

/// <summary>One message as the list shows it.</summary>
public sealed class Line
{
    public Line(string id, string who, string text, DateTimeOffset when, bool mine, string? channel = null)
    {
        Id = id;
        Who = who;
        Text = text;
        When = when;
        Time = when.LocalDateTime.ToString("HH:mm");
        Mine = mine;
        Channel = channel;
    }

    public string Id { get; }
    public string Who { get; }
    public string Text { get; }
    public DateTimeOffset When { get; }
    public string Time { get; }
    public bool Mine { get; }

    /// <summary>The text channel of a room message; null for a direct message.</summary>
    public string? Channel { get; }

    public HorizontalAlignment Align => Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public IBrush Fill => Resource(Mine ? "BubbleMine" : "BubbleOther");
    public IBrush Ink => Mine ? Brushes.White : Resource("Ink");

    private static IBrush Resource(string key) =>
        Avalonia.Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : Brushes.Gray;
}
