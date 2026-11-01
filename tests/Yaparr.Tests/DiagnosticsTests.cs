using Yaparr.Diagnostics;

namespace Yaparr.Tests;

public class DiagnosticsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-logtest-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 10, 31, 12, 0, 0);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private FileLog Open(int keep = 7, string? legacy = null) => new(_dir, "yaparr", keep, legacy, () => _now);

    private string[] Names() => Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n).ToArray()!;

    [Fact]
    public void Each_day_has_its_own_file_and_old_days_are_removed()
    {
        var log = Open(keep: 3);
        log.Write("first day");
        Assert.EndsWith("yaparr-2026-10-31.log", log.Path);

        for (int i = 1; i <= 4; i++)
        {
            _now = _now.AddDays(1);
            log.Write("day " + i);
        }

        // Three days kept: today and the two before it.
        Assert.Equal(new[] { "yaparr-2026-11-02.log", "yaparr-2026-11-03.log", "yaparr-2026-11-04.log" }, Names());
        Assert.Contains("day 4", File.ReadAllText(log.Path));
    }

    [Fact]
    public void Starting_again_the_same_day_adds_to_the_file_and_removes_what_has_aged_out()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "yaparr-2026-10-01.log"), "old");
        File.WriteAllText(Path.Combine(_dir, "yaparr-2026-10-29.log"), "recent");
        File.WriteAllText(Path.Combine(_dir, "other.log"), "not ours");

        Open().Write("one");
        Open().Write("two");

        Assert.Equal(new[] { "other.log", "yaparr-2026-10-29.log", "yaparr-2026-10-31.log" }, Names());
        var lines = File.ReadAllLines(Path.Combine(_dir, "yaparr-2026-10-31.log"));
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("one", lines[0]);
        Assert.EndsWith("two", lines[1]);
    }

    [Fact]
    public void A_log_from_before_the_split_becomes_the_file_of_the_day_it_was_last_written()
    {
        Directory.CreateDirectory(_dir);
        var legacy = Path.Combine(_dir, "yaparr.log");
        File.WriteAllText(legacy, "an old line\n");
        File.SetLastWriteTime(legacy, new DateTime(2026, 10, 30, 8, 0, 0));

        Open(legacy: legacy).Write("new line");

        Assert.False(File.Exists(legacy));
        Assert.Equal("an old line\n", File.ReadAllText(Path.Combine(_dir, "yaparr-2026-10-30.log")));
        Assert.Contains("new line", File.ReadAllText(Path.Combine(_dir, "yaparr-2026-10-31.log")));
    }

    [Fact]
    public void The_tail_reaches_back_into_yesterday_when_today_is_short()
    {
        var log = Open();
        log.Write("yesterday 1");
        log.Write("yesterday 2");
        _now = _now.AddDays(1);
        log.Write("today 1");

        var tail = log.Tail(10);
        Assert.Equal(3, tail.Count);
        Assert.EndsWith("yesterday 1", tail[0]);
        Assert.EndsWith("today 1", tail[2]);
        Assert.Equal(2, log.Tail(2).Count);
        Assert.EndsWith("today 1", log.Tail(1).Single());
    }

    [Fact]
    public void A_day_stops_growing_at_its_limit_and_says_so_once()
    {
        var log = Open();
        var big = new string('x', 1_000_000);
        for (int i = 0; i < FileLog.MaxBytesPerDay / big.Length + 5; i++) log.Write(big);

        var size = new FileInfo(log.Path).Length;
        Assert.InRange(size, FileLog.MaxBytesPerDay - 2_000_000, FileLog.MaxBytesPerDay + 1000);
        Assert.Contains("reached", File.ReadLines(log.Path).Last());

        // The next day writes again.
        _now = _now.AddDays(1);
        log.Write("fresh");
        Assert.Contains("fresh", File.ReadAllText(log.Path));
    }

    [Theory]
    [InlineData("connected to 203.0.113.77:5000 ok", "connected to 203.0.x.x:5000 ok")]
    [InlineData("from 192.168.1.20", "from 192.168.x.x")]
    [InlineData("at 2001:db8:85a3:0:0:8a2e:370:7334 up", "at 2001:x up")]
    [InlineData("fe80::1 and ::1", "fe80:x and ::x")]
    public void Addresses_are_shortened(string input, string expected) => Assert.Equal(expected, LogScrubber.Scrub(input));

    [Fact]
    public void Ids_are_shortened_and_keys_and_passwords_are_removed()
    {
        var id = new string('a', 8) + new string('b', 56);
        Assert.Equal("aaaaaaaa… joined", LogScrubber.Scrub(id + " joined"));

        var invite = "Gk3bX9vQ2mZpLw8RtYh1NcVd5Fs7JqAeUo4iKx0BnMy"; // an invite is base64url, 43 characters or more
        Assert.Equal("room [removed] opened", LogScrubber.Scrub($"room {invite} opened"));

        var wireGuardKey = "yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=";
        Assert.DoesNotContain("yAnz5TF", LogScrubber.Scrub("key " + wireGuardKey));

        Assert.Equal("password: [removed] and credential=[removed]", LogScrubber.Scrub("password: hunter2 and credential=abc123"));
    }

    [Fact]
    public void Times_versions_and_paths_are_left_alone()
    {
        const string line = "2026-10-31 12:34:56.789 Yaparr 1.7.1 on 10.0.26200 loaded C:\\Users\\someone\\AppData\\Local\\Yaparr\\history\\file.bin and /usr/lib/x86_64-linux-gnu/libSomething.so.1";
        Assert.Equal(line, LogScrubber.Scrub(line));
    }
}
