using System.Security.Cryptography;
using System.Threading.Channels;
using Yaparr.Chat.Files;
using Yaparr.Identity;

namespace Yaparr.Tests;

public sealed class FileSharingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-files-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = new();

    public FileSharingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static PeerId Id(int n) => new(string.Concat(Enumerable.Repeat(n.ToString("x2"), 32)));

    private string MakeFile(string name, int size)
    {
        var path = Path.Combine(_dir, name);
        var bytes = new byte[size];
        RandomNumberGenerator.Fill(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ---- the offer, the hashes and the pieces

    [Fact]
    public void An_offer_is_carried_in_a_message_and_read_back_and_nonsense_is_plain_text()
    {
        var offer = new FileOffer(new string('a', 64), "holiday.mov", 5_000_000_000, FileOffer.DefaultPieceSize);
        Assert.True(FileOffer.TryParse(offer.ToText("look at this"), out var read, out var caption));
        Assert.Equal(offer, read);
        Assert.Equal("look at this", caption);
        Assert.Equal(4769, read.PieceCount);

        Assert.False(FileOffer.TryParse("hello", out _, out var plain));
        Assert.Equal("hello", plain);
        foreach (var bad in new[]
                 {
                     new FileOffer(new string('a', 63), "x", 10, FileOffer.DefaultPieceSize),         // not a root
                     new FileOffer(new string('a', 64), "x", 0, FileOffer.DefaultPieceSize),          // empty
                     new FileOffer(new string('a', 64), "x", 10, 1000),                               // piece size not allowed
                     new FileOffer(new string('a', 64), "x", 10, 3 * 1024 * 1024),                    // not a power of two
                     new FileOffer(new string('a', 64), "x", 1L << 50, FileOffer.MinPieceSize),       // too many pieces to hold
                 })
            Assert.False(FileOffer.TryParse(bad.ToText(), out _, out _));
    }

    [Fact]
    public void A_file_name_is_made_safe_to_show_and_to_save_as()
    {
        Assert.Equal("passwd", FileOffer.CleanName("../../etc/passwd"));
        Assert.Equal("evil.exe", FileOffer.CleanName("C:\\Windows\\evil.exe"));
        Assert.Equal("hidden", FileOffer.CleanName("..hidden"));
        Assert.Equal("file", FileOffer.CleanName("   "));
        Assert.Equal(120, FileOffer.CleanName(new string('x', 500)).Length);
    }

    [Fact]
    public void Large_files_get_larger_pieces_so_the_list_of_hashes_stays_small()
    {
        Assert.Equal(1 << 20, FileOffer.PieceSizeFor(1));
        Assert.Equal(1 << 20, FileOffer.PieceSizeFor(1L << 40));                 // 1 TiB is a million pieces
        Assert.Equal(2 << 20, FileOffer.PieceSizeFor(5L << 40));
        Assert.True(FileOffer.IsValid(5L << 40, FileOffer.PieceSizeFor(5L << 40)));
    }

    [Fact]
    public async Task The_root_fixes_the_size_and_every_piece_and_any_change_is_noticed()
    {
        var path = MakeFile("a.bin", 2_500_000);
        var (offer, hashes) = await PieceHashes.HashFileAsync(path);
        Assert.Equal(3, offer.PieceCount);
        Assert.Equal(3 * 32, hashes.Length);
        Assert.True(PieceHashes.Matches(offer, hashes));

        var changed = (byte[])hashes.Clone();
        changed[40] ^= 1;
        Assert.False(PieceHashes.Matches(offer, changed));
        Assert.False(PieceHashes.Matches(offer, hashes.AsSpan(0, 64)));
        Assert.False(PieceHashes.Matches(offer with { Size = offer.Size - 1 }, hashes));            // the size is part of the root
        Assert.NotEqual(offer.Root, PieceHashes.RootOf(offer.Size, offer.PieceSize * 2, hashes));    // and so is the piece size

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(PieceHashes.PieceMatches(hashes, 1, bytes.AsSpan(offer.PieceSize, offer.PieceSize)));
        Assert.False(PieceHashes.PieceMatches(hashes, 0, bytes.AsSpan(offer.PieceSize, offer.PieceSize)));
    }

    [Fact]
    public void A_piece_map_counts_what_is_held_and_survives_being_written_down()
    {
        var map = new PieceMap(13);
        Assert.True(map.Set(0));
        Assert.True(map.Set(12));
        Assert.False(map.Set(12));
        Assert.False(map.Set(13));
        Assert.Equal(2, map.Count);
        Assert.True(PieceMap.TryFromBytes(13, map.ToBytes(), out var back));
        Assert.True(back[0] && back[12] && !back[5]);
        Assert.Equal(2, back.Count);
        Assert.False(PieceMap.TryFromBytes(13, new byte[3], out _));
    }

    // ---- a partial file on disk

    [Fact]
    public async Task A_partial_file_keeps_only_pieces_that_match_and_picks_up_where_it_stopped()
    {
        var path = MakeFile("p.bin", 3_300_000);
        var (offer, hashes) = await PieceHashes.HashFileAsync(path);
        var bytes = await File.ReadAllBytesAsync(path);
        var dir = Path.Combine(_dir, "part");

        using (var part = PartialFile.OpenOrCreate(dir, offer))
        {
            Assert.False(part.TryWritePiece(0, bytes.AsSpan(0, offer.PieceSize)));                // the hashes are not known yet
            Assert.False(part.SetHashes(new byte[hashes.Length]));                                // not the list the root names
            Assert.True(part.SetHashes(hashes));
            Assert.False(part.TryWritePiece(1, bytes.AsSpan(0, offer.PieceSize)));                // the wrong bytes for piece 1
            Assert.True(part.TryWritePiece(2, bytes.AsSpan(2 * offer.PieceSize, offer.PieceLength(2))));
            Assert.True(part.TryWritePiece(0, bytes.AsSpan(0, offer.PieceSize)));
            Assert.True(part.TryWritePiece(3, bytes.AsSpan(3 * offer.PieceSize, offer.PieceLength(3))));
        }

        using var again = PartialFile.OpenOrCreate(dir, offer);
        Assert.True(again.Have[0] && again.Have[2] && again.Have[3] && !again.Have[1]);
        Assert.NotNull(again.Hashes);
        Assert.Equal(offer.PieceSize + offer.PieceLength(2) + offer.PieceLength(3), again.BytesHave);
        Assert.Equal(bytes.AsSpan(0, offer.PieceSize).ToArray(), again.ReadPiece(0));
        Assert.Null(again.ReadPiece(1));

        Assert.True(again.TryWritePiece(1, bytes.AsSpan(offer.PieceSize, offer.PieceSize)));
        var finished = Path.Combine(_dir, "finished.bin");
        again.Finish(finished);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(finished));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task A_record_that_does_not_belong_to_the_offer_is_thrown_away()
    {
        var path = MakeFile("q.bin", 1_100_000);
        var (offer, hashes) = await PieceHashes.HashFileAsync(path);
        var dir = Path.Combine(_dir, "part2");
        using (var part = PartialFile.OpenOrCreate(dir, offer))
        {
            part.SetHashes(hashes);
            part.TryWritePiece(0, (await File.ReadAllBytesAsync(path)).AsSpan(0, offer.PieceSize));
        }
        File.WriteAllText(Path.Combine(dir, "state.json"), "not json");
        using var reopened = PartialFile.OpenOrCreate(dir, offer);
        Assert.Equal(0, reopened.Have.Count);
        Assert.Null(reopened.Hashes);
    }

    // ---- a small network of nodes

    private sealed class Peer : IFilePeer
    {
        private readonly Channel<byte[]> _wire = Channel.CreateUnbounded<byte[]>();
        public readonly Node Owner;
        public readonly Node Target;
        public Peer? Back;
        public volatile bool Up = true;
        public int BlocksSent;
        public int WantsSent;
        public readonly List<(int Piece, int FirstBlock)> Wants = new();
        public Func<byte[], byte[]?>? Tamper;

        public Peer(Node owner, Node target)
        {
            Owner = owner;
            Target = target;
            _ = Task.Run(async () =>
            {
                await foreach (var frame in _wire.Reader.ReadAllAsync())
                    if (Up) Target.Receive(Back!, frame);
            });
        }

        public PeerId Id => Target.Id;
        public bool Metered => false;
        public bool CanRelay => true;
        public long Backlog => 0;

        public void Send(byte[] frame)
        {
            if (!Up) throw new InvalidOperationException("gone");
            if (frame[0] == FileWire.Block) Interlocked.Increment(ref BlocksSent);
            if (Tamper != null && Tamper(frame) is { } changed) frame = changed;
            else if (Tamper != null) return;                       // the connection swallowed it
            if (frame[0] == FileWire.Want)
            {
                lock (Wants) Wants.Add(((int)FileWire.U32(frame, FileWire.Header), FileWire.U16(frame, FileWire.Header + 4)));
                Interlocked.Increment(ref WantsSent);
            }
            _wire.Writer.TryWrite(frame);
        }

        public void Stop() => _wire.Writer.TryComplete();
    }

    private sealed class Node : IDisposable
    {
        public readonly PeerId Id;
        public readonly FileServer Server;
        public readonly Dictionary<string, FileDownload> Downloads = new();
        public readonly Dictionary<string, IPieceStore> Shared = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Peer> _peers = new();

        public Node(int n, int slots = 4)
        {
            Id = Id_(n);
            Server = new FileServer(root => Downloads.TryGetValue(root, out var d) ? d.File : Shared.GetValueOrDefault(root), (_, _) => true, () => slots, () => 0, () => Environment.TickCount64);
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    await Task.Delay(40);
                    foreach (var d in Downloads.Values.ToList()) d.Tick();
                }
            });
        }

        private static PeerId Id_(int n) => FileSharingTests.Id(n);

        public void Receive(Peer from, byte[] frame)
        {
            if (!FileWire.TryRoot(frame, out var root)) return;
            Downloads.TryGetValue(root, out var download);
            switch (frame[0])
            {
                case FileWire.Interest or FileWire.HashesWant or FileWire.Want or FileWire.Cancel:
                    Server.OnFrame(from, frame);
                    break;
                case FileWire.Have or FileWire.HaveMore:
                    Server.OnFrame(from, frame);
                    download?.OnFrame(from, frame);
                    break;
                default:
                    download?.OnFrame(from, frame);
                    break;
            }
        }

        public Peer ConnectTo(Node other, out Peer theirs)
        {
            var mine = new Peer(this, other);
            theirs = new Peer(other, this);
            mine.Back = theirs;
            theirs.Back = mine;
            _peers.Add(mine);
            other._peers.Add(theirs);
            foreach (var d in Downloads.Values) d.AddPeer(mine);
            foreach (var d in other.Downloads.Values) d.AddPeer(theirs);
            return mine;
        }

        public void Cut(Peer mine, Peer theirs)
        {
            mine.Up = theirs.Up = false;
            foreach (var d in Downloads.Values) d.RemovePeer(mine.Id);
            foreach (var d in mine.Target.Downloads.Values) d.RemovePeer(theirs.Id);
            Server.PeerGone(mine.Id);
            mine.Target.Server.PeerGone(theirs.Id);
        }

        public FileDownload Download(FileOffer offer, string dir, FileDownloadOptions? options = null)
        {
            var download = new FileDownload(PartialFile.OpenOrCreate(dir, offer), () => Environment.TickCount64, options ?? Fast);
            Downloads[offer.Root] = download;
            foreach (var p in _peers.Where(p => p.Up)) download.AddPeer(p);
            return download;
        }

        public void Dispose()
        {
            _stop.Cancel();
            Server.Dispose();
            foreach (var d in Downloads.Values) d.Dispose();
            foreach (var p in _peers) p.Stop();
        }
    }

    private static readonly FileDownloadOptions Fast = new()
    {
        StallTimeout = TimeSpan.FromMilliseconds(600), Cooldown = TimeSpan.FromMilliseconds(120), InterestRefresh = TimeSpan.FromMilliseconds(300),
    };

    private Node NewNode(int n, int slots = 4)
    {
        var node = new Node(n, slots);
        _disposables.Add(node);
        return node;
    }

    private async Task<(FileOffer Offer, byte[] Bytes)> Share(Node node, string name, int size)
    {
        var path = MakeFile(name, size);
        var (offer, hashes) = await PieceHashes.HashFileAsync(path);
        node.Shared[offer.Root] = new SharedFile(path, offer, hashes);
        return (offer, await File.ReadAllBytesAsync(path));
    }

    private static async Task Finished(FileDownload download, int seconds = 20) => await download.Completion.WaitAsync(TimeSpan.FromSeconds(seconds));

    private async Task<byte[]> Collect(FileDownload download, string name)
    {
        var path = Path.Combine(_dir, "out-" + name);
        download.File.Finish(path);
        return await File.ReadAllBytesAsync(path);
    }

    [Fact]
    public async Task A_file_goes_from_the_sender_to_a_downloader_intact()
    {
        var sender = NewNode(1);
        var (offer, bytes) = await Share(sender, "movie.bin", 5_300_000);
        var me = NewNode(2);
        me.ConnectTo(sender, out _);
        var download = me.Download(offer, Path.Combine(_dir, "d1"));

        await Finished(download);
        Assert.Equal(FileState.Completed, download.State);
        Assert.Equal(bytes, await Collect(download, "movie.bin"));
    }

    [Fact]
    public async Task A_download_waits_with_nobody_connected_and_carries_on_when_someone_turns_up()
    {
        var sender = NewNode(1);
        var (offer, bytes) = await Share(sender, "w.bin", 2_200_000);
        var me = NewNode(2);
        var download = me.Download(offer, Path.Combine(_dir, "d2"));
        await Task.Delay(300);
        Assert.Equal(FileState.Waiting, download.State);

        me.ConnectTo(sender, out _);
        await Finished(download);
        Assert.Equal(bytes, await Collect(download, "w.bin"));
    }

    [Fact]
    public async Task A_download_that_was_closed_carries_on_from_the_pieces_it_had_and_asks_only_for_the_rest()
    {
        var sender = NewNode(1);
        var (offer, bytes) = await Share(sender, "r.bin", 8_000_000);
        var dir = Path.Combine(_dir, "d3");

        // First go: let some pieces arrive, then the app "closes".
        var first = NewNode(2);
        var link = first.ConnectTo(sender, out var theirs);
        int perPiece = offer.PieceSize / FileOffer.BlockSize, sent = 0;
        theirs.Tamper = frame => frame[0] != FileWire.Block || Interlocked.Increment(ref sent) <= 3 * perPiece + 10 ? frame : null;   // the connection dies in piece four
        var one = first.Download(offer, dir);
        await WaitUntil(() => Volatile.Read(ref sent) > 3 * perPiece + 10);
        await WaitUntil(() => one.File.Have.Count == 3);
        first.Cut(link, theirs);
        int had = one.File.Have.Count;
        var missingBefore = Enumerable.Range(0, offer.PieceCount).Where(p => !one.File.Have[p]).ToHashSet();
        one.Dispose();
        one.File.Dispose();
        Assert.Equal(3, had);

        // Second go, a new run of the app: the same folder.
        var second = NewNode(3);
        var link2 = second.ConnectTo(sender, out _);
        var two = second.Download(offer, dir);
        Assert.Equal(had, two.File.Have.Count);
        await Finished(two);

        // It asked for nothing it already had.
        Assert.NotEmpty(link2.Wants);
        Assert.All(link2.Wants, w => Assert.Contains(w.Piece, missingBefore));
        Assert.Equal(bytes, await Collect(two, "r.bin"));
    }

    [Fact]
    public async Task When_the_holder_goes_away_in_the_middle_a_piece_is_finished_by_someone_else_from_the_block_where_it_stopped()
    {
        var a = NewNode(1);
        var b = NewNode(2);
        var (offer, bytes) = await Share(a, "m.bin", 3_000_000);
        b.Shared[offer.Root] = a.Shared[offer.Root];
        var me = NewNode(3);

        // A's connection dies after a few blocks (it swallows everything after them); B has the whole file.
        var fromA = me.ConnectTo(a, out var aToMe);
        int delivered = 0;
        aToMe.Tamper = frame => frame[0] != FileWire.Block || Interlocked.Increment(ref delivered) <= 5 ? frame : null;
        var download = me.Download(offer, Path.Combine(_dir, "d4"));
        await WaitUntil(() => Volatile.Read(ref delivered) > 5);
        await Task.Delay(100);
        Assert.Equal(0, download.File.Have.Count);          // the first piece has only five of its blocks
        me.Cut(fromA, aToMe);
        var toB = me.ConnectTo(b, out var bToMe);

        await Finished(download);
        Assert.Equal(bytes, await Collect(download, "m.bin"));
        Assert.True(bToMe.BlocksSent > 0);
        // B was asked for the rest of that piece, not the whole of it.
        Assert.Contains(toB.Wants, w => w.FirstBlock > 0);
    }

    [Fact]
    public async Task A_holder_that_sends_wrong_data_is_set_aside_and_the_file_still_arrives_from_an_honest_one()
    {
        var liar = NewNode(1);
        var honest = NewNode(2);
        var (offer, bytes) = await Share(liar, "lie.bin", 3_500_000);
        honest.Shared[offer.Root] = liar.Shared[offer.Root];
        var me = NewNode(3);
        me.ConnectTo(liar, out var liarToMe);
        liarToMe.Tamper = frame => { if (frame[0] != FileWire.Block) return frame; var copy = (byte[])frame.Clone(); copy[^1] ^= 0xFF; return copy; };
        var download = me.Download(offer, Path.Combine(_dir, "d5"));
        await Task.Delay(300);                          // the liar gets a chance first
        me.ConnectTo(honest, out _);

        await Finished(download);
        Assert.Equal(bytes, await Collect(download, "lie.bin"));
    }

    [Fact]
    public async Task A_holder_that_makes_up_the_piece_list_is_not_believed()
    {
        var liar = NewNode(1);
        var honest = NewNode(2);
        var (offer, bytes) = await Share(liar, "list.bin", 2_300_000);
        honest.Shared[offer.Root] = liar.Shared[offer.Root];
        var me = NewNode(3);
        me.ConnectTo(liar, out var liarToMe);
        // What the liar sends as the list of piece hashes is changed on the way; its pieces are untouched.
        liarToMe.Tamper = frame => { if (frame[0] != FileWire.HashesData) return frame; var copy = (byte[])frame.Clone(); copy[^1] ^= 0xFF; return copy; };
        var download = me.Download(offer, Path.Combine(_dir, "d6"));
        await Task.Delay(300);                          // the liar is asked for the list first, and the list does not fit the root
        Assert.Equal(0, download.File.Have.Count);
        Assert.Null(download.File.Hashes);
        me.ConnectTo(honest, out _);
        await Finished(download);
        Assert.Equal(bytes, await Collect(download, "list.bin"));
    }

    [Fact]
    public async Task A_sender_with_one_place_hands_the_rest_to_the_downloaders_who_already_have_pieces()
    {
        var sender = NewNode(1, slots: 1);
        var (offer, bytes) = await Share(sender, "swarm.bin", 6_000_000);
        var b = NewNode(2, slots: 1);
        var c = NewNode(3, slots: 1);
        var d = NewNode(4, slots: 1);
        b.ConnectTo(sender, out var senderToB);
        c.ConnectTo(sender, out var senderToC);
        d.ConnectTo(sender, out var senderToD);
        b.ConnectTo(c, out _);
        b.ConnectTo(d, out _);
        c.ConnectTo(d, out _);

        var downloads = new[] { b.Download(offer, Path.Combine(_dir, "sb")), c.Download(offer, Path.Combine(_dir, "sc")), d.Download(offer, Path.Combine(_dir, "sd")) };
        foreach (var dl in downloads) await Finished(dl, 40);

        int blocksPerCopy = offer.PieceCount * (offer.PieceSize / FileOffer.BlockSize);
        int fromSender = senderToB.BlocksSent + senderToC.BlocksSent + senderToD.BlocksSent;
        Assert.True(fromSender < 3 * blocksPerCopy, $"the sender sent {fromSender} blocks; three copies would be {3 * blocksPerCopy}");
        Assert.Equal(bytes, await Collect(downloads[0], "sb"));
        Assert.Equal(bytes, await Collect(downloads[1], "sc"));
        Assert.Equal(bytes, await Collect(downloads[2], "sd"));
    }

    [Fact]
    public async Task A_paused_download_asks_for_nothing_and_goes_on_when_resumed()
    {
        var sender = NewNode(1);
        var (offer, bytes) = await Share(sender, "pause.bin", 4_000_000);
        var me = NewNode(2);
        var link = me.ConnectTo(sender, out var theirs);
        var download = me.Download(offer, Path.Combine(_dir, "d7"));
        download.Pause();
        await Task.Delay(200);
        int wants = theirs.WantsSent + link.WantsSent;
        await Task.Delay(300);
        Assert.Equal(FileState.Paused, download.State);
        Assert.Equal(wants, theirs.WantsSent + link.WantsSent);

        download.Resume();
        await Finished(download);
        Assert.Equal(bytes, await Collect(download, "pause.bin"));
    }

    [Fact]
    public async Task A_file_that_changed_on_the_senders_disk_is_not_sent_as_if_it_were_the_offered_one()
    {
        var sender = NewNode(1);
        var (offer, _) = await Share(sender, "chg.bin", 2_300_000);
        var shared = (SharedFile)sender.Shared[offer.Root];
        var bytes = await File.ReadAllBytesAsync(shared.Path);
        bytes[100] ^= 1;
        await File.WriteAllBytesAsync(shared.Path, bytes);
        Assert.Null(shared.ReadPiece(0));
        Assert.NotNull(shared.ReadPiece(1));
    }

    [Fact]
    public async Task Only_those_who_may_have_a_file_are_answered()
    {
        var closed = new FileServer(_ => null, (_, _) => true, () => 1, () => 0, () => Environment.TickCount64);
        _disposables.Add(closed);
        var sent = new List<byte[]>();
        var peer = new FakeSend(Id(9), sent);
        closed.OnFrame(peer, FileWire.Simple(FileWire.Interest, new string('b', 64)));
        Assert.Equal(FileWire.Gone, Assert.Single(sent)[0]);

        var sender = NewNode(1);
        var (offer, _) = await Share(sender, "priv.bin", 1_200_000);
        var picky = new FileServer(root => sender.Shared.GetValueOrDefault(root), (id, _) => id == Id(5), () => 1, () => 0, () => Environment.TickCount64);
        _disposables.Add(picky);
        sent.Clear();
        picky.OnFrame(peer, FileWire.Simple(FileWire.Interest, offer.Root));
        Assert.Equal(FileWire.Gone, Assert.Single(sent)[0]);
        sent.Clear();
        picky.OnFrame(new FakeSend(Id(5), sent), FileWire.Simple(FileWire.Interest, offer.Root));
        Assert.Equal(FileWire.Have, Assert.Single(sent)[0]);
    }

    private sealed class FakeSend(PeerId id, List<byte[]> sent) : IFilePeer
    {
        public PeerId Id => id;
        public bool Metered => false;
        public bool CanRelay => false;
        public long Backlog => 0;
        public void Send(byte[] frame) { lock (sent) sent.Add(frame); }
    }

    private static async Task WaitUntil(Func<bool> condition, int seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("the condition was never met");
            await Task.Delay(10);
        }
    }
}
