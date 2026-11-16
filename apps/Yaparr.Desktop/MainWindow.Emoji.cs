using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaparr.Chat;

namespace Yaparr.Desktop;

/// <summary>An emoji offered in the list that opens when you type a :shortcode: a standard one (its emoji) or a custom one (its picture, written as its code).</summary>
public sealed record EmojiSuggestion(string Emoji, string Code, Avalonia.Media.IImage? Image = null)
{
    public bool HasImage => Image != null;

    /// <summary>What goes into the message: the emoji, or for a custom one its :name:.</summary>
    public string Insert => Image != null ? Code : Emoji;
}

// Emoji in the message box: a :shortcode that is being typed offers the emoji that match (Enter or Tab takes the one selected), and a whole :shortcode:
// becomes its emoji as the closing colon is typed. The button next to the box opens the picker, in the message box's own mode (see ComposerEmoji_Click).
public partial class MainWindow
{
    private int _emojiStart = -1;
    private bool _replacingEmoji;

    private void SetUpEmojiTyping()
    {
        InputBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty || e.Property == TextBox.CaretIndexProperty) UpdateEmojiSuggest();
        };
        // Clicking the list takes the focus from the box for a moment; that must not close it before the click counts.
        InputBox.LostFocus += (_, _) => { if (!EmojiSuggestList.IsPointerOver) CloseEmojiSuggest(); };
        EmojiSuggestList.PointerReleased += (_, _) => { if (EmojiSuggestList.SelectedItem != null) AcceptEmojiSuggest(); };
    }

    private void UpdateEmojiSuggest()
    {
        if (_replacingEmoji) return;
        var text = InputBox.Text ?? "";
        int caret = Math.Clamp(InputBox.CaretIndex, 0, text.Length);
        if (EmojiTyping.PendingAt(text, caret) is not { } pending) { CloseEmojiSuggest(); return; }

        if (pending.Closed)
        {
            // :thumbs_up: written out is the emoji. A name that is not one is left as typed (a custom emoji is written as :name: and stays so).
            if (EmojiTyping.FromShortcode(pending.Name) is { } exact) ReplaceInInput(pending.Start, caret, exact.Emoji);
            else CloseEmojiSuggest();
            return;
        }

        // Your own emoji and the room's come first, then the standard ones.
        var choices = CustomMatches(pending.Name, 5).Where(c => c.Image != null).Select(c => new EmojiSuggestion("", ":" + c.Name + ":", c.Image)).ToList();
        choices.AddRange(EmojiTyping.Complete(pending.Name, 8).Select(f => new EmojiSuggestion(f.Entry.Emoji, ":" + f.Code + ":")).Take(Math.Max(3, 8 - choices.Count)));
        if (choices.Count == 0) { CloseEmojiSuggest(); return; }
        _emojiStart = pending.Start;
        EmojiSuggestList.ItemsSource = choices;
        EmojiSuggestList.SelectedIndex = 0;
        EmojiSuggestPopup.IsOpen = true;
    }

    private void CloseEmojiSuggest()
    {
        EmojiSuggestPopup.IsOpen = false;
        _emojiStart = -1;
    }

    // Puts text in place of a part of the message box and the caret after it, without the box taking that for typing.
    private void ReplaceInInput(int start, int end, string with)
    {
        _replacingEmoji = true;
        try
        {
            var text = InputBox.Text ?? "";
            start = Math.Clamp(start, 0, text.Length);
            end = Math.Clamp(end, start, text.Length);
            InputBox.Text = text[..start] + with + text[end..];
            InputBox.CaretIndex = start + with.Length;
        }
        finally { _replacingEmoji = false; }
        CloseEmojiSuggest();
    }

    private void AcceptEmojiSuggest()
    {
        if (EmojiSuggestList.SelectedItem is not EmojiSuggestion choice || _emojiStart < 0) return;
        var text = InputBox.Text ?? "";
        ReplaceInInput(_emojiStart, Math.Clamp(InputBox.CaretIndex, _emojiStart, text.Length), choice.Insert);
        InputBox.Focus();
    }

    private void EmojiSuggest_DoubleTapped(object? sender, TappedEventArgs e) => AcceptEmojiSuggest();

    // The keys of the list while it is open. True when the key was used.
    private bool HandleEmojiKey(KeyEventArgs e)
    {
        if (!EmojiSuggestPopup.IsOpen) return false;
        int count = (EmojiSuggestList.ItemsSource as IReadOnlyList<EmojiSuggestion>)?.Count ?? 0;
        switch (e.Key)
        {
            case Key.Down when count > 0: EmojiSuggestList.SelectedIndex = (EmojiSuggestList.SelectedIndex + 1) % count; return true;
            case Key.Up when count > 0: EmojiSuggestList.SelectedIndex = (EmojiSuggestList.SelectedIndex - 1 + count) % count; return true;
            case Key.Enter or Key.Tab: AcceptEmojiSuggest(); return true;
            case Key.Escape: CloseEmojiSuggest(); return true;
        }
        return false;
    }

    // The emoji picked in the picker goes into what is being written, in place of what is selected.
    private void InsertIntoInput(string emoji)
    {
        var text = InputBox.Text ?? "";
        int a = Math.Clamp(Math.Min(InputBox.SelectionStart, InputBox.SelectionEnd), 0, text.Length);
        int b = Math.Clamp(Math.Max(InputBox.SelectionStart, InputBox.SelectionEnd), a, text.Length);
        ReplaceInInput(a, b, emoji);
        InputBox.Focus();
    }
}
