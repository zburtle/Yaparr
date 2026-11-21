using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Yaparr.AppCore;

namespace Yaparr.Desktop;

/// <summary>
/// "Learn more about …" under a setting: a click opens the guide for its <see cref="Topic"/> (see <see cref="Guides"/>) in the browser. Settings say what they do in a line, and
/// the explanation lives in the guide. Give it a <c>Foreground</c> of the accent colour where it is used.
/// </summary>
public sealed class LearnMoreLink : TextBlock
{
    public static readonly StyledProperty<string?> TopicProperty = AvaloniaProperty.Register<LearnMoreLink, string?>(nameof(Topic));

    public string? Topic
    {
        get => GetValue(TopicProperty);
        set => SetValue(TopicProperty, value);
    }

    // It is a TextBlock for styling.
    protected override Type StyleKeyOverride => typeof(TextBlock);

    public LearnMoreLink()
    {
        TextDecorations = Avalonia.Media.TextDecorations.Underline;
        Cursor = new Cursor(StandardCursorType.Hand);
        Background = Brushes.Transparent;
        FontSize = 12;
        Margin = new Thickness(0, 2, 0, 4);
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        Tapped += async (_, _) =>
        {
            if (Guides.Url(Topic) is not { } guide || TopLevel.GetTopLevel(this) is not { } top) return;
            try { await top.Launcher.LaunchUriAsync(guide); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { }
        };
    }
}
