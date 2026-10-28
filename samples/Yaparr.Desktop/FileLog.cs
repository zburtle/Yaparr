using System.Diagnostics;
using System.IO;

namespace Yaparr.Desktop;

public sealed class FileLog
{
    private const long MaxBytes = 2_000_000;
    private readonly object _gate = new();

    public FileLog(string path)
    {
        Path = path;
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Delete(path);
        }
        catch (IOException) { }
    }

    public string Path { get; }

    public void Write(string line)
    {
        Trace.WriteLine("[yaparr] " + line);
        lock (_gate)
        {
            try { File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
