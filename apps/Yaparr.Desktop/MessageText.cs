using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Yaparr.Chat;

namespace Yaparr.Desktop;

/// <summary>The words of a message, selectable, with the names of the people it mentions standing out.</summary>
public sealed class MessageText : SelectableTextBlock
{
    public static readonly StyledProperty<string?> BodyProperty = AvaloniaProperty.Register<MessageText, string?>(nameof(Body));
    public static readonly StyledProperty<IReadOnlyList<string>?> MarksProperty = AvaloniaProperty.Register<MessageText, IReadOnlyList<string>?>(nameof(Marks));

    public string? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

    /// <summary>Pieces of the words to make stand out, for example "@Alice".</summary>
    public IReadOnlyList<string>? Marks { get => GetValue(MarksProperty); set => SetValue(MarksProperty, value); }

    /// <summary>A web address in the words was clicked (not while selecting words).</summary>
    public static readonly RoutedEvent<LinkClickedEventArgs> LinkClickedEvent = RoutedEvent.Register<MessageText, LinkClickedEventArgs>(nameof(LinkClicked), RoutingStrategies.Bubble);

    public event EventHandler<LinkClickedEventArgs>? LinkClicked { add => AddHandler(LinkClickedEvent, value); remove => RemoveHandler(LinkClickedEvent, value); }

    private IReadOnlyList<LinkPreview.Span> _links = Array.Empty<LinkPreview.Span>();

    // It looks and styles like any selectable text block.
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BodyProperty || change.Property == MarksProperty) Rebuild();
    }

    private void Rebuild()
    {
        var body = Body ?? "";
        Inlines ??= new InlineCollection();
        Inlines.Clear();
        _links = LinkPreview.LinksIn(body);
        int at = 0;
        void Words(string text)
        {
            foreach (var (part, marked) in Split(text, Marks))
                Inlines.Add(marked
                    ? new Run(part) { FontWeight = FontWeight.Bold, Background = new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x80)) }
                    : new Run(part));
        }
        foreach (var link in _links)
        {
            if (link.Start > at) Words(body[at..link.Start]);
            Inlines.Add(new Run(body.Substring(link.Start, link.Length)) { TextDecorations = Avalonia.Media.TextDecorations.Underline });
            at = link.Start + link.Length;
        }
        if (at < body.Length || _links.Count == 0) Words(body[at..]);
    }

    // The address under a point of the text, or null.
    private string? LinkAt(Point point)
    {
        if (_links.Count == 0) return null;
        var hit = TextLayout.HitTestPoint(point);
        if (!hit.IsInside) return null;
        int index = hit.TextPosition;
        foreach (var link in _links)
            if (index >= link.Start && index < link.Start + link.Length) return link.Url;
        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Cursor = _links.Count > 0 && LinkAt(e.GetPosition(this)) != null ? new Cursor(StandardCursorType.Hand) : null;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left || SelectionStart != SelectionEnd) return;
        if (LinkAt(e.GetPosition(this)) is { } url) RaiseEvent(new LinkClickedEventArgs(LinkClickedEvent, this, url));
    }

    /// <summary>Cuts the words at the marks. A mark counts where it is found, in any case; where two overlap the earlier one wins.</summary>
    internal static List<(string Text, bool Marked)> Split(string body, IReadOnlyList<string>? marks)
    {
        var parts = new List<(string, bool)>();
        int at = 0;
        while (at < body.Length)
        {
            int best = -1, length = 0;
            if (marks != null)
                foreach (var mark in marks)
                {
                    if (mark.Length < 2) continue;
                    int i = body.IndexOf(mark, at, StringComparison.OrdinalIgnoreCase);
                    if (i >= 0 && (best < 0 || i < best || (i == best && mark.Length > length))) { best = i; length = mark.Length; }
                }
            if (best < 0) { parts.Add((body[at..], false)); break; }
            if (best > at) parts.Add((body[at..best], false));
            parts.Add((body.Substring(best, length), true));
            at = best + length;
        }
        if (parts.Count == 0) parts.Add(("", false));
        return parts;
    }
}

/// <summary>Which address was clicked.</summary>
public sealed class LinkClickedEventArgs : RoutedEventArgs
{
    public LinkClickedEventArgs(RoutedEvent routedEvent, object source, string url) : base(routedEvent, source) => Url = url;

    public string Url { get; }
}
