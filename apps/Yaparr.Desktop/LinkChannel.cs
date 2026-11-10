using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Yaparr.Desktop;

/// <summary>
/// How a second launch (the operating system opening a <c>yaparr://</c> link) hands the link to the copy that is already
/// running: one line over a pipe that only the same user can use. There is one pipe per data folder, so profiles started with
/// <c>--data</c> do not meet. What arrives is untrusted text from another program; the listener only passes it on, and the
/// app parses it strictly and asks before acting on it.
/// </summary>
internal sealed class LinkChannel : IDisposable
{
    /// <summary>What a second launch with no link sends: bring the window to the front.</summary>
    public const string ShowLine = "show";

    /// <summary>
    /// A second launch also says which version it is ("version 1.2.3", as a second line, so a copy that does not know it ignores it and still
    /// shows its window). A copy that is older than the one that was started is what is left running after an update; it offers to restart.
    /// </summary>
    public const string VersionPrefix = "version ";

    private const int MaxLine = 1024;
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<string> _onLine;
    private readonly string _name;

    private LinkChannel(string name, Action<string> onLine)
    {
        _name = name;
        _onLine = onLine;
    }

    public static string NameFor(string dataDirectory)
    {
        var full = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        return "yaparr-link-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16].ToLowerInvariant();
    }

    /// <summary>Hands <paramref name="line"/> to the app that listens on <paramref name="name"/>; false if none does.</summary>
    public static bool TrySend(string name, string line, int timeoutMs = 1000, string? secondLine = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.WriteLine(line.Length > MaxLine ? line[..MaxLine] : line);
            if (secondLine != null) writer.WriteLine(secondLine.Length > MaxLine ? secondLine[..MaxLine] : secondLine);
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Starts listening, unless another copy already does. Each line that arrives is passed to <paramref name="onLine"/> on a worker thread.</summary>
    public static LinkChannel? Listen(string name, Action<string> onLine)
    {
        // Another copy of the same profile listening means a second one would only take its place on some systems.
        if (TrySend(name, "", timeoutMs: 150)) return null;
        var channel = new LinkChannel(name, onLine);
        NamedPipeServerStream first;
        try { first = channel.NewServer(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        _ = Task.Run(() => channel.RunAsync(first));
        return channel;
    }

    private NamedPipeServerStream NewServer() =>
        new(_name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task RunAsync(NamedPipeServerStream server)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(_stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                using var reader = new StreamReader(server, new UTF8Encoding(false), false, 2048, leaveOpen: true);
                var line = await reader.ReadLineAsync(timeout.Token);
                if (!string.IsNullOrWhiteSpace(line) && line.Length <= MaxLine) _onLine(line);
                // A second line, if there is one, is the version of the copy that was started (see VersionPrefix); nothing else is read.
                var second = line == null ? null : await reader.ReadLineAsync(timeout.Token);
                if (second is { Length: <= MaxLine } && second.StartsWith(VersionPrefix, StringComparison.Ordinal)) _onLine(second);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }

            server.Dispose();
            if (_stop.IsCancellationRequested) break;
            try { server = NewServer(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { break; }
        }
        server.Dispose();
    }

    public void Dispose() => _stop.Cancel();
}
