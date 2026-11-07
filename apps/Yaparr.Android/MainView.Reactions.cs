using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Yaparr.Chat;
using Button = Avalonia.Controls.Button;

namespace Yaparr.AndroidApp;

// Emoji reactions: a tap on an emoji under a message adds yours or takes it back; "React" in a message's long-press menu, or the + under it, opens the
// panel with the quick emoji and a search through all of them.
public partial class MainView
{
    // The app's own font has a plain heart (and a few more symbols that are also emoji), which would be drawn instead of the colour one; the system's font
    // does not, so the text falls through to the emoji font.
    private static readonly FontFamily EmojiFont = new("Roboto");

    private Line? _reactingTo;
    private bool _reactWired;
    private int _reactGroup;

    private void ReactionChipTapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.DataContext is not ReactionChip chip) return;
        if (chip.IsAdd) { OpenReact(chip.Owner); return; }
        _ = ReactAsync(chip.Owner, chip.Emoji, on: !chip.Mine);
    }

    private async Task ReactAsync(Line line, string emoji, bool on)
    {
        if (_session == null || _current is not { } chat) return;
        try { await _session.ReactAsync(chat, line, emoji, on); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { ShowChatNote(chat, "Couldn't react: " + ex.Message); }
    }

    private void OpenReact(Line line)
    {
        _reactingTo = line;
        if (!_reactWired) WireReactPanel();
        ReactSearch.Text = "";
        ShowReactGroup(0);
        ReactPanel.IsVisible = true;
    }

    private Button ReactButton(string emoji, double size)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = emoji, FontSize = size, FontFamily = EmojiFont },
            Padding = new Thickness(8, 6),
            Margin = new Thickness(1),
            Background = Brushes.Transparent,
            Tag = emoji,
        };
        button.Click += ReactPicked;
        return button;
    }

    private void WireReactPanel()
    {
        _reactWired = true;
        foreach (var emoji in EmojiData.Quick) ReactQuick.Children.Add(ReactButton(emoji, 26));

        for (int i = 0; i < EmojiData.Groups.Count; i++)
        {
            int index = i;
            var tab = new Button
            {
                Content = new TextBlock { Text = ReactGroupIcon(EmojiData.Groups[i].Name), FontSize = 20, FontFamily = EmojiFont },
                Padding = new Thickness(10, 4),
                Background = Brushes.Transparent,
            };
            tab.Click += (_, _) => { ReactSearch.Text = ""; ShowReactGroup(index); };
            ReactTabs.Children.Add(tab);
        }

        ReactSearch.TextChanged += (_, _) =>
        {
            var query = ReactSearch.Text ?? "";
            if (query.Trim().Length == 0) { ShowReactGroup(_reactGroup); return; }
            var found = EmojiData.Search(query, 120);
            ReactTitle.Text = found.Count == 0 ? "Nothing found" : found.Count + " emoji";
            FillReactGrid(found);
        };
    }

    private static string ReactGroupIcon(string group) => group switch
    {
        "Smileys & Emotion" => "\U0001F600",
        "People & Body" => "\U0001F44B",
        "Animals & Nature" => "\U0001F43B",
        "Food & Drink" => "\U0001F354",
        "Travel & Places" => "✈️",
        "Activities" => "⚽",
        "Objects" => "\U0001F4A1",
        "Symbols" => "\U0001F523",
        "Flags" => "\U0001F3C1",
        _ => "❓",
    };

    private void ShowReactGroup(int index)
    {
        _reactGroup = Math.Clamp(index, 0, EmojiData.Groups.Count - 1);
        var group = EmojiData.Groups[_reactGroup];
        ReactTitle.Text = group.Name;
        FillReactGrid(group.Entries);
    }

    private void FillReactGrid(IReadOnlyList<EmojiEntry> entries)
    {
        ReactGrid.Children.Clear();
        foreach (var entry in entries) ReactGrid.Children.Add(ReactButton(entry.Emoji, 24));
    }

    private void ReactPicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not string emoji || _reactingTo is not { } line) return;
        ReactPanel.IsVisible = false;
        // Picking one you already have takes it off, as tapping it under the message does.
        bool has = line.Reactions.Any(r => r.Emoji == emoji && r.Mine);
        _ = ReactAsync(line, emoji, on: !has);
    }

    private void ReactCloseClicked(object? sender, RoutedEventArgs e) => ReactPanel.IsVisible = false;
}
