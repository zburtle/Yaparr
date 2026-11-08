using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Yaparr.Chat;

namespace Yaparr.AndroidApp;

/// <summary>The words of a message, with each web address underlined and tappable.</summary>
public sealed class LinkText : TextBlock
{
    public static readonly StyledProperty<string?> BodyProperty = AvaloniaProperty.Register<LinkText, string?>(nameof(Body));

    /// <summary>A web address was tapped.</summary>
    public static readonly RoutedEvent<LinkTappedEventArgs> LinkTappedEvent = RoutedEvent.Register<LinkText, LinkTappedEventArgs>(nameof(LinkTapped), RoutingStrategies.Bubble);

    public string? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

    public event EventHandler<LinkTappedEventArgs>? LinkTapped { add => AddHandler(LinkTappedEvent, value); remove => RemoveHandler(LinkTappedEvent, value); }

    private IReadOnlyList<LinkPreview.Span> _links = Array.Empty<LinkPreview.Span>();

    // It looks and styles like any text block.
    protected override Type StyleKeyOverride => typeof(TextBlock);

    public LinkText() => Tapped += OnTapped;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BodyProperty) Rebuild();
    }

    private void Rebuild()
    {
        var body = Body ?? "";
        _links = LinkPreview.LinksIn(body);
        if (_links.Count == 0) { Inlines?.Clear(); Text = body; return; }
        Text = null;
        Inlines ??= new InlineCollection();
        Inlines.Clear();
        int at = 0;
        foreach (var link in _links)
        {
            if (link.Start > at) Inlines.Add(new Run(body[at..link.Start]));
            Inlines.Add(new Run(body.Substring(link.Start, link.Length)) { TextDecorations = Avalonia.Media.TextDecorations.Underline });
            at = link.Start + link.Length;
        }
        if (at < body.Length) Inlines.Add(new Run(body[at..]));
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (_links.Count == 0) return;
        var hit = TextLayout.HitTestPoint(e.GetPosition(this));
        if (!hit.IsInside) return;
        foreach (var link in _links)
            if (hit.TextPosition >= link.Start && hit.TextPosition < link.Start + link.Length)
            {
                e.Handled = true;
                RaiseEvent(new LinkTappedEventArgs(LinkTappedEvent, this, link.Url));
                return;
            }
    }
}

/// <summary>Which address was tapped.</summary>
public sealed class LinkTappedEventArgs : RoutedEventArgs
{
    public LinkTappedEventArgs(RoutedEvent routedEvent, object source, string url) : base(routedEvent, source) => Url = url;

    public string Url { get; }
}
