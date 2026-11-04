using Yaparr.Desktop;
using Yaparr.Diagnostics;

namespace Yaparr.Tests;

public class CrashLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-crash-" + Guid.NewGuid().ToString("N"));

    public CrashLogTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Text(FileLog log) => File.ReadAllText(log.Path);

    [Fact]
    public void AnExceptionIsWrittenWithItsStackAndInnerException()
    {
        var log = new FileLog(_dir);
        var crash = new CrashLog(log, Path.Combine(_dir, ".running"));
        Exception caught;
        try { try { throw new InvalidOperationException("inner trouble"); } catch (Exception inner) { throw new IOException("outer trouble", inner); } }
        catch (Exception ex) { caught = ex; }
        crash.Report("unhandled exception", caught, ending: true);
        var text = Text(log);
        Assert.Contains("unhandled exception", text);
        Assert.Contains("outer trouble", text);
        Assert.Contains("inner trouble", text);
        Assert.Contains("AnExceptionIsWrittenWithItsStackAndInnerException", text);
    }

    [Fact]
    public void ARunThatEndedBeforeClosingIsNotedAtTheNextStart()
    {
        var log = new FileLog(_dir);
        var marker = Path.Combine(_dir, ".running");
        File.WriteAllText(marker, "999999 2026-11-03 20:00:00");   // a process that is not running
        new CrashLog(log, marker).Start();
        Assert.Contains("did not end by closing", Text(log));
    }

    [Fact]
    public void AClosedRunLeavesNothingToReport()
    {
        var log = new FileLog(_dir);
        var marker = Path.Combine(_dir, ".running");
        var first = new CrashLog(log, marker);
        first.Start();
        Assert.True(File.Exists(marker));
        first.Stop();
        Assert.False(File.Exists(marker));
        new CrashLog(log, marker).Start();
        Assert.DoesNotContain("did not end by closing", Text(log));
    }

    [Fact]
    public void ACrashKeepsTheMarkerSoTheNextStartSaysSo()
    {
        var log = new FileLog(_dir);
        var marker = Path.Combine(_dir, ".running");
        var crash = new CrashLog(log, marker);
        crash.Start();
        crash.Report("the program is ending", new Exception("boom"), ending: true);
        crash.Stop();
        Assert.True(File.Exists(marker));
        Assert.DoesNotContain("Yaparr closing", Text(log));
    }
}
