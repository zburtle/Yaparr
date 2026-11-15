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

    /// <summary>The pictures of custom emoji by name: a <c>:name:</c> in the words that has one is drawn as the picture, a little taller than the words.</summary>
    public static readonly StyledProperty<IReadOnlyDictionary<string, IImage>?> EmojiImagesProperty =
        AvaloniaProperty.Register<LinkText, IReadOnlyDictionary<string, IImage>?>(nameof(EmojiImages));

    public IReadOnlyDictionary<string, IImage>? EmojiImages { get => GetValue(EmojiImagesProperty); set => SetValue(EmojiImagesProperty, value); }

    public string? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

    public event EventHandler<LinkTappedEventArgs>? LinkTapped { add => AddHandler(LinkTappedEvent, value); remove => RemoveHandler(LinkTappedEvent, value); }

    private IReadOnlyList<LinkPreview.Span> _links = Array.Empty<LinkPreview.Span>();

    // It looks and styles like any text block.
    protected override Type StyleKeyOverride => typeof(TextBlock);

    public LinkText() => Tapped += OnTapped;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BodyProperty || change.Property == EmojiImagesProperty || change.Property == FontSizeProperty) Rebuild();
    }

    private void Rebuild()
    {
        var body = Body ?? "";
        _links = LinkPreview.LinksIn(body);
        var images = EmojiImages is { Count: > 0 } found ? found : null;
        if (_links.Count == 0 && images == null) { Inlines?.Clear(); Text = body; return; }
        Text = null;
        Inlines ??= new InlineCollection();
        Inlines.Clear();
        // A custom emoji that has arrived is its picture; one that has not stays as the :name: it was written.
        void Words(string words)
        {
            if (images == null) { Inlines.Add(new Run(words)); return; }
            foreach (var (piece, name) in CustomEmojiRules.Pieces(words))
            {
                if (name != null && images.TryGetValue(name, out var picture))
                    Inlines.Add(new InlineUIContainer(new Image { Source = picture, Height = FontSize * 1.4, Stretch = Stretch.Uniform, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }));
                else Inlines.Add(new Run(piece));
            }
        }
        int at = 0;
        foreach (var link in _links)
        {
            if (link.Start > at) Words(body[at..link.Start]);
            Inlines.Add(new Run(body.Substring(link.Start, link.Length)) { TextDecorations = Avalonia.Media.TextDecorations.Underline });
            at = link.Start + link.Length;
        }
        if (at < body.Length) Words(body[at..]);
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
