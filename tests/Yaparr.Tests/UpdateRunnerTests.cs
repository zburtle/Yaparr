using System.Diagnostics;
using Yaparr.Desktop;
using Yaparr.Updates;

namespace Yaparr.Tests;

// What happens around an update on Linux (the app closes, the installer runs, the app starts again) and how a copy that is older than the one that was
// just started hears about it.
public class UpdateRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-runner-" + Guid.NewGuid().ToString("N"));

    public UpdateRunnerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string? FindShell()
    {
        foreach (var candidate in new[] { "/bin/sh", "/usr/bin/sh", @"C:\Program Files\Git\bin\sh.exe", @"C:\Program Files\Git\usr\bin\sh.exe" })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static string Slashes(string path) => path.Replace('\\', '/');

    // Runs the helper with a process id that is long gone (so it does not wait), a stand-in for the app that records how it was started, and the given
    // installer command. Returns what the app was started with, or null if it was not started.
    private (int Exit, string? Started, string Output) Run(string shell, params string[] installer)
    {
        var app = Path.Combine(_dir, "app.sh");
        var marker = Slashes(Path.Combine(_dir, "started.txt"));
        File.WriteAllText(app, "#!/bin/sh\necho \"restarted=$YAPARR_RESTARTED failed=$YAPARR_UPDATE_FAILED\" > '" + marker + "'\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(app, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var info = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-c", UpdateVerifier.LinuxUpdateRunnerScript, "test-runner", "2147483646", Slashes(app) }) info.ArgumentList.Add(arg);
        foreach (var arg in installer) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        var recorded = Path.Combine(_dir, "started.txt");
        return (process.ExitCode, File.Exists(recorded) ? File.ReadAllText(recorded).Trim() : null, output);
    }

    [Fact]
    public void After_an_update_the_app_is_started_again_as_a_restart_with_no_failure()
    {
        if (FindShell() is not { } shell) return;
        var record = Slashes(Path.Combine(_dir, "installer-args.txt"));
        // The installer gets its arguments exactly as given, including ones with spaces.
        var (exit, started, output) = Run(shell, shell, "-c", "printf '%s|%s' \"$1\" \"$2\" > '" + record + "'", "installer", "a b", "c");

        Assert.True(exit == 0, output);
        Assert.Equal("restarted=1 failed=", started);
        Assert.Equal("a b|c", File.ReadAllText(Path.Combine(_dir, "installer-args.txt")));
    }

    [Fact]
    public void An_installer_that_fails_still_starts_the_app_again_and_marks_the_failure()
    {
        if (FindShell() is not { } shell) return;
        var (exit, started, output) = Run(shell, shell, "-c", "exit 1");

        Assert.True(exit == 0, output);
        Assert.Equal("restarted=1 failed=1", started);
    }

    [Fact]
    public void The_update_helper_script_has_no_carriage_returns()
    {
        Assert.DoesNotContain('\r', UpdateVerifier.LinuxUpdateRunnerScript);
    }

    // ---- a newer copy tells the running one ----

    [Fact]
    public async Task A_second_launch_tells_the_running_copy_which_version_it_is_without_a_copy_that_does_not_know_this_being_confused()
    {
        var name = LinkChannel.NameFor(Path.Combine(Path.GetTempPath(), "yaparr-link-test-" + Guid.NewGuid().ToString("N")));
        var lines = new List<string>();
        var both = new TaskCompletionSource();
        using var channel = LinkChannel.Listen(name, line => { lock (lines) { lines.Add(line); if (lines.Count == 2) both.TrySetResult(); } });
        Assert.NotNull(channel);

        Assert.True(LinkChannel.TrySend(name, LinkChannel.ShowLine, 1000, LinkChannel.VersionPrefix + "9.9.9"));
        await both.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The first line is what every version understands (bring the window to the front); the second is the version.
        Assert.Equal(new[] { LinkChannel.ShowLine, "version 9.9.9" }, lines);
    }

    [Fact]
    public async Task A_plain_link_still_arrives_as_one_line()
    {
        var name = LinkChannel.NameFor(Path.Combine(Path.GetTempPath(), "yaparr-link-test-" + Guid.NewGuid().ToString("N")));
        var got = new TaskCompletionSource<string>();
        using var channel = LinkChannel.Listen(name, line => got.TrySetResult(line));
        Assert.NotNull(channel);

        Assert.True(LinkChannel.TrySend(name, LinkChannel.ShowLine));
        Assert.Equal(LinkChannel.ShowLine, await got.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
