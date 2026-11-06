using System.Net;
using Yaparr.Discovery;

internal sealed class LocalFileDiscovery : IPeerDiscovery
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "yaparr-local-discovery");
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(45);

    private readonly int _port;
    private readonly HashSet<string> _announced = new();

    public LocalFileDiscovery(int port) => _port = port;

    public Task AnnounceAsync(byte[] topic, CancellationToken ct = default)
    {
        var dir = Path.Combine(Root, Convert.ToHexString(topic));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, _port.ToString());
        File.WriteAllText(file, DateTime.UtcNow.ToString("O"));
        lock (_announced) _announced.Add(file);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IPEndPoint>> FindPeersAsync(byte[] topic, CancellationToken ct = default)
    {
        var found = new List<IPEndPoint>();
        var dir = Path.Combine(Root, Convert.ToHexString(topic));
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (!int.TryParse(Path.GetFileName(file), out var port) || port == _port) continue;
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > Fresh) { TryDelete(file); continue; }
                found.Add(new IPEndPoint(IPAddress.Loopback, port));
            }
        }
        return Task.FromResult<IReadOnlyList<IPEndPoint>>(found);
    }

    public void Dispose()
    {
        lock (_announced) foreach (var file in _announced) TryDelete(file);
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (IOException) { }
    }
}
