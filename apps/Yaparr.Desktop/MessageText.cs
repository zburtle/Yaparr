using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Yaparr.Desktop;

/// <summary>The words of a message, selectable, with the names of the people it mentions standing out.</summary>
public sealed class MessageText : SelectableTextBlock
{
    public static readonly StyledProperty<string?> BodyProperty = AvaloniaProperty.Register<MessageText, string?>(nameof(Body));
    public static readonly StyledProperty<IReadOnlyList<string>?> MarksProperty = AvaloniaProperty.Register<MessageText, IReadOnlyList<string>?>(nameof(Marks));

    public string? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

    /// <summary>Pieces of the words to make stand out, for example "@Alice".</summary>
    public IReadOnlyList<string>? Marks { get => GetValue(MarksProperty); set => SetValue(MarksProperty, value); }

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
        foreach (var (part, marked) in Split(body, Marks))
            Inlines.Add(marked
                ? new Run(part) { FontWeight = FontWeight.Bold, Background = new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x80)) }
                : new Run(part));
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
