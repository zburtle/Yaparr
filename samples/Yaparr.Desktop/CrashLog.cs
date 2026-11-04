using System.Diagnostics;
using Avalonia.Threading;
using Yaparr.Diagnostics;

namespace Yaparr.Desktop;

/// <summary>
/// Gets what happens when the app dies into the log. A managed exception that nothing catches ends the program, from any thread,
/// and used to leave nothing in the log; now it is written first. A crash that cannot be caught (in native code, such as the
/// graphics or the audio) leaves no exception to write, so a marker file says the program is running: if it is still there at
/// the next start, the last run did not end by closing, and the log says so.
/// </summary>
public sealed class CrashLog
{
    private readonly FileLog _log;
    private readonly string _marker;
    private bool _crashed;

    public CrashLog(FileLog log, string markerPath)
    {
        _log = log;
        _marker = markerPath;
    }

    /// <summary>Notes a run that did not end by closing, then marks this run as started.</summary>
    public void Start()
    {
        try
        {
            if (File.Exists(_marker) && int.TryParse(File.ReadAllText(_marker).Split(' ')[0], out int pid) && pid != Environment.ProcessId && !IsRunning(pid))
                _log.Write($"the last run (process {pid}) did not end by closing: it crashed, was killed or lost power. If it was a crash, nothing more was written about it.", important: true);
            File.WriteAllText(_marker, $"{Environment.ProcessId} {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Writes an exception with its whole chain, and which thread it was on.</summary>
    public void Report(string what, Exception? exception, bool ending = false)
    {
        if (ending) _crashed = true;
        _log.Write($"{what} (thread {Environment.CurrentManagedThreadId}{(Thread.CurrentThread.Name is { } name ? " " + name : "")}):{Environment.NewLine}{exception?.ToString() ?? "no details"}", important: true);
    }

    /// <summary>The program is closing normally: the marker goes, unless an exception has already ended it.</summary>
    public void Stop()
    {
        if (_crashed) return;
        _log.Write("Yaparr closing");
        try { File.Delete(_marker); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Started with the program, before the log exists: exceptions that come before <see cref="Attach"/> go to a file in the temporary
    /// folder, since the data folder may not be settled yet.
    /// </summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Current(e.IsTerminating ? "the program is ending: unhandled exception" : "unhandled exception", e.ExceptionObject as Exception, e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) => Current("a task failed and nobody was waiting for it", e.Exception, false);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _current?.Stop();
    }

    private static CrashLog? _current;

    /// <summary>From here on exceptions go to the app's own log.</summary>
    public static void Attach(FileLog log, string logFolder)
    {
        var crash = new CrashLog(log, Path.Combine(logFolder, ".running"));
        crash.Start();
        _current = crash;
        Dispatcher.UIThread.UnhandledException += (_, e) => crash.Report("exception on the interface thread", e.Exception, ending: true);
    }

    private static void Current(string what, Exception? exception, bool ending)
    {
        if (_current != null) { _current.Report(what, exception, ending); return; }
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "yaparr-crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {what}:{Environment.NewLine}{exception}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
