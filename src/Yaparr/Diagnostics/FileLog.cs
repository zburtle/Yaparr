using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Yaparr.Diagnostics;

/// <summary>
/// A log with one file per day (<c>yaparr-2026-10-31.log</c>) that keeps the last few days, so a problem is never cut off in the middle
/// and the log of the day before a bug report is still there.
/// </summary>
public sealed class FileLog
{
    /// <summary>How many days of logs are kept, today included.</summary>
    public const int DefaultKeepDays = 7;

    /// <summary>A day's file stops growing here, so a program stuck in a loop cannot fill the disk.</summary>
    public const long MaxBytesPerDay = 20_000_000;

    private readonly string _directory;
    private readonly string _prefix;
    private readonly int _keepDays;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private DateTime _day;
    private string _path = "";
    private long _size;
    private bool _full;

    /// <param name="directory">Where the files go; created if needed.</param>
    /// <param name="prefix">The start of each file name.</param>
    /// <param name="legacyFile">A single log file from before the logs were split by day; it becomes the file of the day it was last written.</param>
    public FileLog(string directory, string prefix = "yaparr", int keepDays = DefaultKeepDays, string? legacyFile = null, Func<DateTime>? now = null)
    {
        _directory = directory;
        _prefix = prefix;
        _keepDays = Math.Max(1, keepDays);
        _now = now ?? (() => DateTime.Now);
        try
        {
            Directory.CreateDirectory(directory);
            if (legacyFile != null) AdoptLegacy(legacyFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        lock (_gate) Roll();
    }

    /// <summary>Today's file.</summary>
    public string Path
    {
        get { lock (_gate) { Roll(); return _path; } }
    }

    /// <summary>The folder holding the log files.</summary>
    public string Folder => _directory;

    public void Write(string line) => Write(line, important: false);

    /// <param name="important">Written even when the day's file has reached its limit, for what must not be lost, such as a crash.</param>
    public void Write(string line, bool important)
    {
        Trace.WriteLine("[yaparr] " + line);
        lock (_gate)
        {
            Roll();
            if (_full && !important) return;
            var text = $"{_now():yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}";
            try
            {
                if (!important && _size + text.Length > MaxBytesPerDay)
                {
                    _full = true;
                    text = $"{_now():yyyy-MM-dd HH:mm:ss.fff} (this day's log reached {MaxBytesPerDay / 1_000_000} MB; nothing more is written today){Environment.NewLine}";
                }
                File.AppendAllText(_path, text);
                _size += Encoding.UTF8.GetByteCount(text);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The last lines written, reaching back into the day before if today has fewer.</summary>
    public IReadOnlyList<string> Tail(int lines)
    {
        string today;
        lock (_gate) { Roll(); today = _path; }
        var result = new List<string>();
        foreach (var file in new[] { today, PathFor(_day.AddDays(-1)) })
        {
            if (result.Count >= lines) break;
            var read = ReadLines(file);
            // The older file goes in front of what is already collected.
            result.InsertRange(0, read.Skip(Math.Max(0, read.Count - (lines - result.Count))));
        }
        return result;
    }

    private static List<string> ReadLines(string file)
    {
        var lines = new List<string>();
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            // Only the tail is wanted; a bounded queue keeps a large file from being held whole.
            var window = new Queue<string>();
            while ((line = reader.ReadLine()) != null)
            {
                window.Enqueue(line);
                if (window.Count > 5000) window.Dequeue();
            }
            lines.AddRange(window);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return lines;
    }

    private string PathFor(DateTime day) => System.IO.Path.Combine(_directory, $"{_prefix}-{day:yyyy-MM-dd}.log");

    // Called with the lock held. A new day starts a new file and removes the ones that have aged out.
    private void Roll()
    {
        var today = _now().Date;
        if (today == _day && _path.Length > 0) return;
        _day = today;
        _path = PathFor(today);
        _full = false;
        try { _size = File.Exists(_path) ? new FileInfo(_path).Length : 0; }
        catch (IOException) { _size = 0; }
        Tidy();
    }

    private void Tidy()
    {
        try
        {
            var oldest = _day.AddDays(-(_keepDays - 1));
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, $"{_prefix}-*.log"))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                if (DateTime.TryParseExact(name[(_prefix.Length + 1)..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < oldest)
                {
                    try { File.Delete(file); } catch (IOException) { }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void AdoptLegacy(string legacyFile)
    {
        if (!File.Exists(legacyFile)) return;
        var target = PathFor(File.GetLastWriteTime(legacyFile).Date);
        if (File.Exists(target)) File.Delete(legacyFile);
        else File.Move(legacyFile, target);
    }
}
