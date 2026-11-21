using System.Text.Json;

namespace Yaparr.Chat.Files;

/// <summary>Where an offer was posted: the rooms (by name) and the people (accounts) in whose chat it was. A file is only given to someone who is in one of them.</summary>
internal sealed class FileScope
{
    public HashSet<string> Rooms { get; set; } = new();
    public HashSet<string> Accounts { get; set; } = new();

    public bool IsEmpty => Rooms.Count == 0 && Accounts.Count == 0;
}

/// <summary>
/// What this device remembers about files, in <c>files.json</c> and <c>hashes/</c> in the files folder: the files it shares from where they are, and the downloads that are under
/// way (so that a restart picks them up again, from the pieces they have). Nothing here is the file itself.
/// </summary>
internal sealed class FileLibrary
{
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly Dictionary<string, SharedFile> _shared = new();
    private readonly Dictionary<string, FileScope> _scopes = new();
    private readonly Dictionary<string, DownloadRecord> _downloads = new();

    private readonly Func<string, Stream?>? _open;

    public FileLibrary(string directory, Func<string, Stream?>? open = null)
    {
        _open = open;
        _directory = directory;
        Directory.CreateDirectory(directory);
        Load();
    }

    public string PartialDirectory(string root) => Path.Combine(_directory, "partial", root);

    /// <summary>Where a finished download waits while it is copied to a place that is not a path.</summary>
    public string FinishedPath(string root) => Path.Combine(_directory, "finished", root);

    public sealed record DownloadRecord(FileOffer Offer, string Destination, bool Paused);

    public SharedFile? Shared(string root) { lock (_gate) return _shared.GetValueOrDefault(root); }

    public IReadOnlyList<DownloadRecord> Downloads() { lock (_gate) return _downloads.Values.ToList(); }

    public FileScope ScopeOf(string root)
    {
        lock (_gate) return _scopes.TryGetValue(root, out var s) ? new FileScope { Rooms = new(s.Rooms), Accounts = new(s.Accounts) } : new FileScope();
    }

    public bool InScope(string root, Func<FileScope, bool> test)
    {
        lock (_gate) return _scopes.TryGetValue(root, out var s) && test(s);
    }

    public void AddScope(string root, string? room, string? account)
    {
        lock (_gate)
        {
            if (!_scopes.TryGetValue(root, out var scope)) _scopes[root] = scope = new FileScope();
            bool changed = room != null && scope.Rooms.Add(room);
            changed |= account != null && scope.Accounts.Add(account);
            if (changed && (_shared.ContainsKey(root) || _downloads.ContainsKey(root))) Save();
        }
    }

    public void AddShared(string path, FileOffer offer, byte[] hashes)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.Combine(_directory, "hashes"));
            File.WriteAllBytes(Path.Combine(_directory, "hashes", offer.Root), hashes);
            _shared[offer.Root] = new SharedFile(path, offer, hashes, _open);
            Save();
        }
    }

    public void RemoveShared(string root)
    {
        lock (_gate)
        {
            if (_shared.Remove(root)) Save();
        }
    }

    public void SetDownload(FileOffer offer, string destination, bool paused)
    {
        lock (_gate)
        {
            _downloads[offer.Root] = new DownloadRecord(offer, destination, paused);
            Save();
        }
    }

    public void ForgetDownload(string root)
    {
        lock (_gate)
        {
            if (_downloads.Remove(root)) Save();
        }
    }

    private void Load()
    {
        try
        {
            var path = Path.Combine(_directory, "files.json");
            if (!File.Exists(path) || JsonSerializer.Deserialize<Dto>(File.ReadAllText(path)) is not { } dto) return;
            foreach (var e in dto.Shared ?? new())
            {
                var offer = new FileOffer(e.Root ?? "", FileOffer.CleanName(e.Name), e.Size, e.PieceSize);
                if (!FileOffer.IsRoot(offer.Root) || !FileOffer.IsValid(offer.Size, offer.PieceSize) || e.Path == null) continue;
                try
                {
                    using (var source = SharedFile.OpenSource(e.Path, _open))
                        if (source == null || source.Length != offer.Size) continue;
                    var hashes = File.ReadAllBytes(Path.Combine(_directory, "hashes", offer.Root));
                    if (!PieceHashes.Matches(offer, hashes)) continue;
                    _shared[offer.Root] = new SharedFile(e.Path, offer, hashes, _open);
                    _scopes[offer.Root] = new FileScope { Rooms = new(e.Rooms ?? new()), Accounts = new(e.Accounts ?? new()) };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            foreach (var e in dto.Downloads ?? new())
            {
                var offer = new FileOffer(e.Root ?? "", FileOffer.CleanName(e.Name), e.Size, e.PieceSize);
                if (!FileOffer.IsRoot(offer.Root) || !FileOffer.IsValid(offer.Size, offer.PieceSize) || string.IsNullOrEmpty(e.Destination)) continue;
                _downloads[offer.Root] = new DownloadRecord(offer, e.Destination, e.Paused);
                _scopes[offer.Root] = new FileScope { Rooms = new(e.Rooms ?? new()), Accounts = new(e.Accounts ?? new()) };
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }

    private void Save()
    {
        try
        {
            var dto = new Dto
            {
                Shared = _shared.Values.Select(s => Entry(s.Offer, s.Path, null, false)).ToList(),
                Downloads = _downloads.Values.Select(d => Entry(d.Offer, null, d.Destination, d.Paused)).ToList(),
            };
            var temp = Path.Combine(_directory, "files.json.tmp");
            File.WriteAllText(temp, JsonSerializer.Serialize(dto));
            File.Move(temp, Path.Combine(_directory, "files.json"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private EntryDto Entry(FileOffer offer, string? path, string? destination, bool paused)
    {
        _scopes.TryGetValue(offer.Root, out var scope);
        return new EntryDto
        {
            Root = offer.Root, Name = offer.Name, Size = offer.Size, PieceSize = offer.PieceSize, Path = path, Destination = destination, Paused = paused,
            Rooms = scope?.Rooms.ToList(), Accounts = scope?.Accounts.ToList(),
        };
    }

    private sealed class Dto
    {
        public List<EntryDto>? Shared { get; set; }
        public List<EntryDto>? Downloads { get; set; }
    }

    private sealed class EntryDto
    {
        public string? Root { get; set; }
        public string? Name { get; set; }
        public long Size { get; set; }
        public int PieceSize { get; set; }
        public string? Path { get; set; }
        public string? Destination { get; set; }
        public bool Paused { get; set; }
        public List<string>? Rooms { get; set; }
        public List<string>? Accounts { get; set; }
    }
}
