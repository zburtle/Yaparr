using Yaparr.Updates;

namespace Yaparr.Tests;

public class ReleaseNotesTests
{
    [Fact]
    public void A_release_as_semantic_release_writes_it_becomes_plain_groups_of_short_lines()
    {
        var sections = ReleaseNotes.Parse(
            "# [1.19.0](https://github.com/zburtle/Yaparr/compare/v1.18.0...v1.19.0) (2026-11-03)\r\n\r\n\r\n" +
            "### Features\r\n\r\n" +
            "* open a direct chat from the People list ([#80](https://github.com/zburtle/Yaparr/issues/80)) ([5e86701](https://github.com/zburtle/Yaparr/commit/5e8670108efd4c94263d6e7c05f9b380b7d24ce9))\r\n" +
            "* **voice:** keep `Opus` on the same channel ([abc1234](https://github.com/zburtle/Yaparr/commit/abc1234))\r\n\r\n" +
            "### Bug Fixes\r\n\r\n" +
            "* the [update dialog](https://example.com/x) reads better\r\n  on two lines\r\n\r\n" +
            "### BREAKING CHANGES\r\n\r\n" +
            "* the old setting is gone\r\n");

        Assert.Equal(new[] { "New", "Fixed", "Changes to be aware of" }, sections.Select(s => s.Title));
        Assert.Equal(new[] { "Open a direct chat from the People list", "Voice: keep Opus on the same channel" }, sections[0].Items);
        Assert.Equal(new[] { "The update dialog reads better on two lines" }, sections[1].Items);
        Assert.Equal(new[] { "The old setting is gone" }, sections[2].Items);
        Assert.DoesNotContain(sections.SelectMany(s => s.Items), i => i.Contains("http") || i.Contains("](") || i.Contains('*') || i.Contains('`'));
    }

    [Fact]
    public void Odd_or_empty_text_gives_something_harmless()
    {
        Assert.Empty(ReleaseNotes.Parse(null));
        Assert.Empty(ReleaseNotes.Parse("   \n\n"));
        Assert.Empty(ReleaseNotes.Parse("# [1.0.1](https://x/compare) (2026-01-01)"));

        var free = ReleaseNotes.Parse("A short note by hand.\n\n* and a bullet");
        var section = Assert.Single(free);
        Assert.Equal("", section.Title);
        Assert.Equal(new[] { "A short note by hand.", "And a bullet" }, section.Items);

        var long_ = ReleaseNotes.Parse("* " + new string('x', 1000));
        Assert.Equal(300, long_[0].Items[0].Length);
        Assert.Equal(40, ReleaseNotes.Parse(string.Join("\n", Enumerable.Range(0, 100).Select(i => "* item " + i)))[0].Items.Count);
    }
}
