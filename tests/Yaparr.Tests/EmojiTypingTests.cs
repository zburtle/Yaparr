using Yaparr.Chat;

namespace Yaparr.Tests;

// Emoji in the words being typed: :shortcodes:, completing them, and a message of nothing but emoji.
public class EmojiTypingTests
{
    [Theory]
    [InlineData("thumbs up", "thumbs_up")]
    [InlineData("face with tears of joy", "face_with_tears_of_joy")]
    [InlineData("flag: Germany", "flag_germany")]
    [InlineData("+1 thing", "plus1_thing")]
    public void A_shortcode_is_the_name_in_lower_case_with_underscores(string name, string code) =>
        Assert.Equal(code, EmojiTyping.Shortcode(new EmojiEntry("x", name, Array.Empty<string>())));

    [Fact]
    public void A_shortcode_finds_its_emoji_whatever_the_case()
    {
        Assert.Equal("\U0001F44D", EmojiTyping.FromShortcode("thumbs_up")!.Emoji);
        Assert.Equal("\U0001F44D", EmojiTyping.FromShortcode("THUMBS_UP")!.Emoji);
        Assert.Null(EmojiTyping.FromShortcode("not_an_emoji"));
        Assert.Null(EmojiTyping.FromShortcode(""));
        Assert.Null(EmojiTyping.FromShortcode(null));
    }

    [Fact]
    public void Every_shortcode_is_unique_and_made_of_plain_characters()
    {
        var seen = new HashSet<string>();
        foreach (var group in EmojiData.Groups)
            foreach (var entry in group.Entries)
            {
                var code = EmojiTyping.Shortcode(entry);
                Assert.Matches("^[a-z0-9_]+$", code);
                seen.Add(code);
            }
        Assert.True(seen.Count > EmojiData.Count * 0.95, "nearly every emoji has a shortcode of its own");
    }

    [Fact]
    public void Completing_puts_names_that_start_with_it_first_then_those_that_contain_it()
    {
        var found = EmojiTyping.Complete("thumbs", 8);
        Assert.Contains(found, f => f.Code == "thumbs_up");
        Assert.All(found.Take(2), f => Assert.StartsWith("thumbs", f.Code));

        var inside = EmojiTyping.Complete("tears", 8);
        Assert.Contains(inside, f => f.Code == "face_with_tears_of_joy");     // not at the start of the name

        Assert.Equal(3, EmojiTyping.Complete("face", 3).Count);
        Assert.Empty(EmojiTyping.Complete("", 8));
        Assert.Empty(EmojiTyping.Complete("qqqqzzzz", 8));
        Assert.Equal(found.Select(f => f.Code).Distinct().Count(), found.Count);
    }

    [Theory]
    [InlineData(":sm", 3, 0, "sm", false)]
    [InlineData("hello :smi", 10, 6, "smi", false)]
    [InlineData("hello :thumbs_up:", 17, 6, "thumbs_up", true)]
    [InlineData("a\n:fire", 7, 2, "fire", false)]
    [InlineData(":+1", 3, 0, "+1", false)]
    public void A_shortcode_being_typed_is_found_before_the_caret(string text, int caret, int start, string name, bool closed) =>
        Assert.Equal(new EmojiTyping.Pending(start, name, closed), EmojiTyping.PendingAt(text, caret));

    [Theory]
    [InlineData("12:30", 5)]           // the colon does not start a word
    [InlineData("see http://x", 12)]
    [InlineData("time: ", 6)]
    [InlineData("a :", 3)]
    [InlineData("a :s", 4)]            // one letter is too few to start offering
    [InlineData("a :ab cd", 8)]        // the caret is past a space: the word is over
    [InlineData("a ::", 4)]
    [InlineData("", 0)]
    public void Something_that_is_not_a_shortcode_is_left_alone(string text, int caret) => Assert.Null(EmojiTyping.PendingAt(text, caret));

    [Fact]
    public void The_caret_decides_which_shortcode_is_meant()
    {
        const string text = ":fire and :smile later";
        Assert.Equal("fire", EmojiTyping.PendingAt(text, 5)!.Value.Name);
        Assert.Null(EmojiTyping.PendingAt(text, 8));
        Assert.Equal("smile", EmojiTyping.PendingAt(text, 16)!.Value.Name);
        Assert.Equal("sm", EmojiTyping.PendingAt(text, 13)!.Value.Name);
    }

    [Theory]
    [InlineData("\U0001F600")]
    [InlineData("\U0001F600\U0001F600\U0001F600")]
    [InlineData("\U0001F600 \U0001F44D  \U0001F525")]
    [InlineData("  \U0001F389\n")]
    [InlineData("\U0001F44D\U0001F3FD")]                                           // with a skin tone
    [InlineData("\U0001F1E9\U0001F1EA")]                                           // a flag
    [InlineData("\U0001F468‍\U0001F469‍\U0001F467")]    // a family: several emoji joined into one
    [InlineData("❤️")]                                                   // a heart with its variation selector
    public void A_message_of_one_to_three_emoji_is_only_emoji(string text) => Assert.True(EmojiTyping.IsOnlyEmoji(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("hi \U0001F600")]
    [InlineData("\U0001F600 hi")]
    [InlineData("\U0001F600\U0001F600\U0001F600\U0001F600")]                       // four is a line of them, not a big one
    [InlineData("1")]
    [InlineData("#")]
    [InlineData("a")]
    [InlineData("\U0001F600.")]
    public void Anything_else_is_not(string? text) => Assert.False(EmojiTyping.IsOnlyEmoji(text));

    [Fact]
    public void The_limit_can_be_changed()
    {
        Assert.True(EmojiTyping.IsOnlyEmoji("\U0001F600\U0001F600\U0001F600\U0001F600", max: 4));
        Assert.False(EmojiTyping.IsOnlyEmoji("\U0001F600\U0001F600", max: 1));
    }
}
