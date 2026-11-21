using Yaparr.AppCore;

namespace Yaparr.Tests;

// The apps link to guides by topic; a link must never point at a page or a heading that is not there.
public class GuidesTests
{
    private static string GuidesDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var guides = Path.Combine(dir.FullName, "docs", "guides");
            if (Directory.Exists(guides)) return guides;
        }
        throw new DirectoryNotFoundException("docs/guides was not found above " + AppContext.BaseDirectory);
    }

    // How GitHub turns a heading into an anchor: lower case, spaces to hyphens, punctuation dropped.
    private static string Anchor(string heading) =>
        new string(heading.Trim().ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-').ToArray()).Replace(' ', '-');

    [Fact]
    public void Every_topic_points_at_a_guide_and_a_heading_that_exist()
    {
        var dir = GuidesDirectory();
        foreach (var (topic, path) in Guides.Topics)
        {
            var parts = path.Split('#');
            var file = Path.Combine(dir, parts[0]);
            Assert.True(File.Exists(file), $"{topic}: {parts[0]} is missing");
            if (parts.Length == 1) continue;
            var anchors = File.ReadLines(file).Where(l => l.StartsWith('#')).Select(l => Anchor(l.TrimStart('#'))).ToHashSet();
            Assert.True(anchors.Contains(parts[1]), $"{topic}: {parts[0]} has no heading for #{parts[1]}");
        }
    }

    [Fact]
    public void A_topic_gives_an_address_in_the_guides_and_an_unknown_one_gives_none()
    {
        Assert.Equal("https://github.com/zburtle/Yaparr/blob/master/docs/guides/yaparr-cloud.md#syncing", Guides.Url("cloud-sync")!.ToString());
        Assert.Null(Guides.Url("nope"));
        Assert.Null(Guides.Url(null));
    }

    [Fact]
    public void Every_guide_is_in_the_index()
    {
        var dir = GuidesDirectory();
        var index = File.ReadAllText(Path.Combine(dir, "README.md"));
        foreach (var file in Directory.GetFiles(dir, "*.md").Select(Path.GetFileName).Where(f => f != "README.md"))
            Assert.Contains("(" + file + ")", index);
    }
}
