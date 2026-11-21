using Yaparr.AppCore;
using Yaparr.Chat.Files;

namespace Yaparr.Tests;

public sealed class FileTextsTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(999L, "999 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(10L * 1024 * 1024, "10 MB")]
    [InlineData(1_500_000_000L, "1.4 GB")]
    [InlineData(120L * 1024 * 1024 * 1024, "120 GB")]
    public void Sizes_are_written_as_people_read_them(long bytes, string text) => Assert.Equal(text, FileTexts.Size(bytes));

    [Fact]
    public void Time_left_is_said_roughly_and_not_at_all_when_it_cannot_be_told()
    {
        Assert.Equal("", FileTexts.Remaining(1_000_000, 100));
        Assert.Equal("", FileTexts.Remaining(0, 5_000_000));
        Assert.Equal("a few seconds left", FileTexts.Remaining(1_000_000, 500_000));
        Assert.Equal("about 40 s left", FileTexts.Remaining(40_000_000, 1_000_000));
        Assert.Equal("about 3 min left", FileTexts.Remaining(180_000_000, 1_000_000));
        Assert.Equal("about 2 h left", FileTexts.Remaining(7_200_000_000, 1_000_000));
    }

    [Fact]
    public void The_status_line_says_what_is_happening_and_who_it_is_waiting_for()
    {
        long mb = 1024 * 1024;
        Assert.Equal("Waiting for someone who has it · 100 MB", FileTexts.Status(new FileProgress("r", FileState.Waiting, 0, 100 * mb, 0, 0)));
        Assert.Equal("Waiting for someone who has it · 50% · 50 MB of 100 MB", FileTexts.Status(new FileProgress("r", FileState.Waiting, 50 * mb, 100 * mb, 0, 0)));
        Assert.Equal("Paused at 25% · 25 MB of 100 MB", FileTexts.Status(new FileProgress("r", FileState.Paused, 25 * mb, 100 * mb, 2, 0)));
        Assert.Equal("100 MB · saved", FileTexts.Status(new FileProgress("r", FileState.Completed, 100 * mb, 100 * mb, 0, 0)));
        var going = FileTexts.Status(new FileProgress("r", FileState.Downloading, 20 * mb, 100 * mb, 3, 2 * mb));
        Assert.StartsWith("20% · 20 MB of 100 MB · 2 MB/s · about 40 s left", going);
        Assert.EndsWith("from 3 people", going);
        Assert.Contains("disk", FileTexts.Status(new FileProgress("r", FileState.Failed, 0, mb, 0, 0)));
    }
}
