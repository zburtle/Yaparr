using System.Text.Json;

namespace Yaparr.Chat.Files;

/// <summary>What a node can serve for one file: the offer, which pieces are held, the piece hashes, and the pieces themselves.</summary>
internal interface IPieceStore
{
    FileOffer Offer { get; }
    PieceMap Have { get; }

    /// <summary>The hashes of all pieces, or null when they are not known yet (a download that has not fetched the list).</summary>
    byte[]? Hashes { get; }

    /// <summary>A piece that is held, checked against its hash, or null (not held, or the file changed since).</summary>
    byte[]? ReadPiece(int piece);
}

/// <summary>A file this device sends from where it is: every piece is held, read from the file when asked for and checked against the hash first.</summary>
internal sealed class SharedFile : IPieceStore
{
    private readonly string _path;
    private readonly Func<string, Stream?>? _open;

    /// <param name="open">Opens the file for something that is not a plain path (a content address on the phone); null means <paramref name="path"/> is a path.</param>
    public SharedFile(string path, FileOffer offer, byte[] hashes, Func<string, Stream?>? open = null)
    {
        _path = path;
        _open = open;
        Offer = offer;
        Hashes = hashes;
        Have = new PieceMap(offer.PieceCount);
        Have.SetAll();
    }

    public string Path => _path;
    public FileOffer Offer { get; }
    public PieceMap Have { get; }
    public byte[]? Hashes { get; }

    /// <summary>Opens a shared file: with the app's opener when the address is one it handles (not a plain path), otherwise as a file.</summary>
    internal static Stream? OpenSource(string path, Func<string, Stream?>? open)
    {
        if (open != null && path.Contains("://", StringComparison.Ordinal)) return open(path);
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public byte[]? ReadPiece(int piece)
    {
        if ((uint)piece >= (uint)Offer.PieceCount) return null;
        try
        {
            using var fs = OpenSource(_path, _open) ?? throw new IOException("The file cannot be opened.");
            if (fs.Length != Offer.Size) return null;
            fs.Position = (long)piece * Offer.PieceSize;
            var data = new byte[Offer.PieceLength(piece)];
            fs.ReadExactly(data);
            return PieceHashes.PieceMatches(Hashes!, piece, data) ? data : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// A file being downloaded, on disk: <c>data.part</c> (the whole file's length, with the verified pieces in place), <c>hashes.bin</c> (the piece hashes, once they are known and
/// have been checked against the root) and <c>state.json</c> (which pieces are in). Every piece is checked before it is written and the record is written after, so what the record
/// says is held is held, and closing the app, a crash or a lost connection costs at most the pieces that were on their way.
/// </summary>
internal sealed class PartialFile : IPieceStore, IDisposable
{
    private readonly string _directory;
    private readonly object _gate = new();
    private FileStream? _data;
    private byte[]? _hashes;

    private PartialFile(string directory, FileOffer offer, PieceMap have, byte[]? hashes)
    {
        _directory = directory;
        Offer = offer;
        Have = have;
        _hashes = hashes;
    }

    public FileOffer Offer { get; }
    public PieceMap Have { get; private set; }
    public byte[]? Hashes { get { lock (_gate) return _hashes; } }
    public string DataPath => System.IO.Path.Combine(_directory, "data.part");
    public bool IsComplete => Have.IsComplete;
    public long BytesHave
    {
        get
        {
            lock (_gate)
            {
                long n = (long)Have.Count * Offer.PieceSize;
                if (Have[Offer.PieceCount - 1]) n -= Offer.PieceSize - Offer.PieceLength(Offer.PieceCount - 1);
                return n;
            }
        }
    }

    /// <summary>Opens what is on disk for this offer, or starts it. A record that does not fit the offer is thrown away.</summary>
    public static PartialFile OpenOrCreate(string directory, FileOffer offer)
    {
        Directory.CreateDirectory(directory);
        var state = System.IO.Path.Combine(directory, "state.json");
        var hashesPath = System.IO.Path.Combine(directory, "hashes.bin");
        var data = System.IO.Path.Combine(directory, "data.part");
        PieceMap? have = null;
        byte[]? hashes = null;
        try
        {
            if (File.Exists(state) && File.Exists(data) && new FileInfo(data).Length == offer.Size
                && JsonSerializer.Deserialize<StateDto>(File.ReadAllText(state)) is { } dto && dto.Root == offer.Root
                && PieceMap.TryFromBytes(offer.PieceCount, Convert.FromBase64String(dto.Have ?? ""), out var map))
            {
                have = map;
                if (File.Exists(hashesPath) && File.ReadAllBytes(hashesPath) is var read && PieceHashes.Matches(offer, read)) hashes = read;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or UnauthorizedAccessException) { have = null; }

        // Without the hashes nothing can be vouched for, so what the record claims is only kept along with them.
        if (have == null || hashes == null) have = new PieceMap(offer.PieceCount);
        var file = new PartialFile(directory, offer, have, hashes);
        file.OpenData();
        file.Save();
        return file;
    }

    private void OpenData()
    {
        _data = new FileStream(DataPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
        if (_data.Length != Offer.Size) _data.SetLength(Offer.Size);
    }

    /// <summary>Takes the piece hashes once they have been fetched. False when they are not the ones the root names.</summary>
    public bool SetHashes(byte[] hashes)
    {
        if (!PieceHashes.Matches(Offer, hashes)) return false;
        lock (_gate)
        {
            _hashes = hashes;
            File.WriteAllBytes(System.IO.Path.Combine(_directory, "hashes.bin"), hashes);
            SaveLocked();
        }
        return true;
    }

    /// <summary>Writes a piece if it is the piece its hash says. Returns false (writing nothing) when it is not, or when the hashes are not known yet.</summary>
    public bool TryWritePiece(int piece, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_hashes == null || _data == null || (uint)piece >= (uint)Offer.PieceCount || data.Length != Offer.PieceLength(piece)) return false;
            if (Have[piece]) return true;
            if (!PieceHashes.PieceMatches(_hashes, piece, data)) return false;
            _data.Position = (long)piece * Offer.PieceSize;
            _data.Write(data);
            _data.Flush();
            Have.Set(piece);
            SaveLocked();
            return true;
        }
    }

    public byte[]? ReadPiece(int piece)
    {
        lock (_gate)
        {
            if (_data == null || _hashes == null || !Have[piece]) return null;
            _data.Position = (long)piece * Offer.PieceSize;
            var data = new byte[Offer.PieceLength(piece)];
            _data.ReadExactly(data);
            return PieceHashes.PieceMatches(_hashes, piece, data) ? data : null;
        }
    }

    /// <summary>Forgets that a piece is held (it was found not to match, or could not be read).</summary>
    public void Forget(int piece)
    {
        lock (_gate)
            if (Have.Clear(piece)) SaveLocked();
    }

    private void Save() { lock (_gate) SaveLocked(); }

    private void SaveLocked()
    {
        var dto = new StateDto { Root = Offer.Root, Name = Offer.Name, Have = Convert.ToBase64String(Have.ToBytes()) };
        var temp = System.IO.Path.Combine(_directory, "state.json.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(dto));
        File.Move(temp, System.IO.Path.Combine(_directory, "state.json"), overwrite: true);
    }

    /// <summary>Moves the finished file to <paramref name="destination"/> and removes what was kept for the download. Only when every piece is held.</summary>
    public void Finish(string destination)
    {
        if (!Have.IsComplete) throw new InvalidOperationException("The file is not complete.");
        lock (_gate)
        {
            _data?.Flush();
            _data?.Dispose();
            _data = null;
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destination))!);
        File.Move(DataPath, destination, overwrite: false);
        try { Directory.Delete(_directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Throws away what was downloaded.</summary>
    public void Delete()
    {
        Dispose();
        try { Directory.Delete(_directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _data?.Dispose();
            _data = null;
        }
    }

    private sealed class StateDto
    {
        public string? Root { get; set; }
        public string? Name { get; set; }
        public string? Have { get; set; }
    }
}
