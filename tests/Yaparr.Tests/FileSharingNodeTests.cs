using System.Net;
using System.Security.Cryptography;
using Yaparr.Chat;
using Yaparr.Chat.Files;
using Yaparr.Discovery;
using Yaparr.Transport;

namespace Yaparr.Tests;

public sealed class FileSharingNodeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-filenodes-" + Guid.NewGuid().ToString("N"));

    public FileSharingNodeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private ChatNode Node(InMemoryDiscoveryHub hub, string name) => new(new ChatNodeOptions
    {
        DisplayName = name,
        EnableRelaying = true,
        RelayFallbackDelay = TimeSpan.FromMilliseconds(500),
        DiscoveryFactory = mux => hub.CreateClient(new IPEndPoint(IPAddress.Loopback, mux.LocalPort)),
        DiscoveryInterval = TimeSpan.FromMilliseconds(300),
        Network = TestUtil.FastNetwork,
        FilesDirectory = Path.Combine(_dir, name + "-files"),
    });

    private ChatNode NodeWithOpener(InMemoryDiscoveryHub hub, string name, Func<string, Stream?> open) => new(new ChatNodeOptions
    {
        DisplayName = name,
        EnableRelaying = true,
        RelayFallbackDelay = TimeSpan.FromMilliseconds(500),
        DiscoveryFactory = mux => hub.CreateClient(new IPEndPoint(IPAddress.Loopback, mux.LocalPort)),
        DiscoveryInterval = TimeSpan.FromMilliseconds(300),
        Network = TestUtil.FastNetwork,
        FilesDirectory = Path.Combine(_dir, name + "-files"),
        OpenSharedFile = open,
    });

    private string MakeFile(string name, int size)
    {
        var path = Path.Combine(_dir, name);
        var bytes = new byte[size];
        RandomNumberGenerator.Fill(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task A_file_offered_in_a_room_moves_nothing_until_someone_downloads_it_and_then_arrives_intact()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = Node(hub, "a");
        using var b = Node(hub, "b");
        var ra = await a.JoinRoomAsync("Files");
        var rb = await b.JoinRoomAsync("Files");
        await TestUtil.WaitFor(() => ra.MemberStates.Count == 1 && rb.MemberStates.Count == 1);

        var path = MakeFile("big.bin", 6_300_000);
        var offer = await a.ShareFileAsync(path, room: ra);
        var got = new TaskCompletionSource<ChatMessage>();
        rb.MessageReceived += m => { if (m.File != null) got.TrySetResult(m); };
        await ra.SendAsync(offer.ToText("the video"));

        var message = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("big.bin", message.File!.Name);
        Assert.Equal("the video", message.FileCaption);
        Assert.Equal(offer, message.File);

        // Nothing is sent until the person asks.
        await Task.Delay(1500);
        Assert.Null(b.FileProgressOf(offer.Root));
        Assert.False(b.HasFile(offer.Root));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "b-files"), "*.part", SearchOption.AllDirectories));

        var saved = Path.Combine(_dir, "downloads", "big.bin");
        Assert.NotNull(b.StartFileDownload(message, saved));
        await TestUtil.WaitFor(() => b.HasFile(offer.Root), timeoutMs: 30_000);
        Assert.Equal(saved, b.FilePath(offer.Root));
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(saved));
    }

    [Fact]
    public async Task A_member_who_finished_the_file_gives_it_to_a_later_one_after_the_sender_has_gone()
    {
        var hub = new InMemoryDiscoveryHub();
        var a = Node(hub, "a");
        using var b = Node(hub, "b");
        var ra = await a.JoinRoomAsync("Relay");
        var rb = await b.JoinRoomAsync("Relay");
        await TestUtil.WaitFor(() => ra.MemberStates.Count == 1 && rb.MemberStates.Count == 1);

        var path = MakeFile("share.bin", 3_300_000);
        var offer = await a.ShareFileAsync(path, room: ra);
        var gotB = new TaskCompletionSource<ChatMessage>();
        rb.MessageReceived += m => { if (m.File != null) gotB.TrySetResult(m); };
        await ra.SendAsync(offer.ToText(""));
        var forB = await gotB.Task.WaitAsync(TimeSpan.FromSeconds(10));
        b.StartFileDownload(forB, Path.Combine(_dir, "b-out", "share.bin"));
        await TestUtil.WaitFor(() => b.HasFile(offer.Root), timeoutMs: 30_000);

        // The sender leaves; a new member comes and asks.
        a.Dispose();
        using var c = Node(hub, "c");
        var rc = await c.JoinRoomAsync("Relay");
        await TestUtil.WaitFor(() => rc.MemberStates.ContainsKey(b.Id), timeoutMs: 20_000);
        var forC = forB with { From = forB.From };       // the same offer, seen in c's history of the room
        Assert.NotNull(c.StartFileDownload(forC, Path.Combine(_dir, "c-out", "share.bin")));
        await TestUtil.WaitFor(() => c.HasFile(offer.Root), timeoutMs: 30_000);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(Path.Combine(_dir, "c-out", "share.bin")));
    }

    [Fact]
    public async Task A_file_offered_in_a_direct_chat_goes_only_to_the_other_person()
    {
        var hub = new InMemoryDiscoveryHub();
        using var alice = Node(hub, "alice");
        using var bob = Node(hub, "bob");
        using var eve = Node(hub, "eve");
        alice.Start(); bob.Start(); eve.Start();
        await Task.Delay(100);
        var incoming = new TaskCompletionSource<DirectChat>();
        bob.IncomingDirectChat += c => incoming.TrySetResult(c);
        var toBob = await alice.OpenDirectChatAsync(bob.Id).WaitAsync(TimeSpan.FromSeconds(10));
        var offer = await alice.ShareFileAsync(MakeFile("dm.bin", 2_200_000), direct: bob.Id);
        var got = new TaskCompletionSource<ChatMessage>();
        var fromAlice = await Task.Run(async () =>
        {
            await toBob.SendAsync("hi");
            var chat = await incoming.Task.WaitAsync(TimeSpan.FromSeconds(10));
            chat.MessageReceived += m => { if (m.File != null) got.TrySetResult(m); };
            await toBob.SendAsync(offer.ToText("for you"));
            return await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        });
        bob.StartFileDownload(fromAlice, Path.Combine(_dir, "bob-out", "dm.bin"));
        await TestUtil.WaitFor(() => bob.HasFile(offer.Root), timeoutMs: 30_000);

        // Eve is connected to Alice (they share a room) and has been told the root, but Alice only gives it to people in the chat it was posted in.
        var rAlice = await alice.JoinRoomAsync("elsewhere");
        var rEve = await eve.JoinRoomAsync("elsewhere");
        await TestUtil.WaitFor(() => rAlice.MemberStates.ContainsKey(eve.Id) && rEve.MemberStates.ContainsKey(alice.Id), timeoutMs: 15_000);
        var seenByEve = fromAlice with { Room = "elsewhere" };
        eve.StartFileDownload(seenByEve, Path.Combine(_dir, "eve-out", "dm.bin"));
        await Task.Delay(4000);
        Assert.False(eve.HasFile(offer.Root));
        Assert.True(bob.HasFile(offer.Root));
    }

    [Fact]
    public async Task A_file_the_phones_chooser_hands_over_as_an_address_is_read_through_the_app_and_sent_like_any_other()
    {
        var hub = new InMemoryDiscoveryHub();
        var content = new byte[2_700_000];
        RandomNumberGenerator.Fill(content);
        // What the phone does: an address, not a path, opened by the app each time something is read.
        Stream? Open(string address) => address == "content://media/42" ? new MemoryStream(content, writable: false) : null;
        using var a = NodeWithOpener(hub, "a", Open);
        using var b = Node(hub, "b");
        var ra = await a.JoinRoomAsync("Phone");
        var rb = await b.JoinRoomAsync("Phone");
        await TestUtil.WaitFor(() => ra.MemberStates.Count == 1 && rb.MemberStates.Count == 1);

        var offer = await a.ShareFileAsync("content://media/42", room: ra, name: "holiday video.mp4");
        Assert.Equal("holiday video.mp4", offer.Name);
        var got = new TaskCompletionSource<ChatMessage>();
        rb.MessageReceived += m => { if (m.File != null) got.TrySetResult(m); };
        await ra.SendAsync(offer.ToText(""));
        var message = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var saved = Path.Combine(_dir, "from-phone", "video.mp4");
        b.StartFileDownload(message, saved);
        await TestUtil.WaitFor(() => b.HasFile(offer.Root), timeoutMs: 30_000);
        Assert.Equal(content, File.ReadAllBytes(saved));
        Assert.Equal("content://media/42", a.FilePath(offer.Root));
    }

    [Fact]
    public async Task A_download_chosen_to_go_to_an_address_is_written_there_and_kept_track_of_there()
    {
        var hub = new InMemoryDiscoveryHub();
        var chosen = new MemoryStream();
        const string place = "content://docs/chosen/1";
        Stream? Open(string address) => address == place ? new MemoryStream(chosen.ToArray(), writable: false) : null;
        using var a = Node(hub, "a");
        using var b = new ChatNode(new ChatNodeOptions
        {
            DisplayName = "b",
            EnableRelaying = true,
            RelayFallbackDelay = TimeSpan.FromMilliseconds(500),
            DiscoveryFactory = mux => hub.CreateClient(new IPEndPoint(IPAddress.Loopback, mux.LocalPort)),
            DiscoveryInterval = TimeSpan.FromMilliseconds(300),
            Network = TestUtil.FastNetwork,
            FilesDirectory = Path.Combine(_dir, "b-files"),
            OpenSharedFile = Open,
            SaveReceivedFile = (address, source) =>
            {
                if (address != place) return false;
                chosen.SetLength(0);
                chosen.Write(File.ReadAllBytes(source));
                return true;
            },
        });
        var ra = await a.JoinRoomAsync("Chosen");
        var rb = await b.JoinRoomAsync("Chosen");
        await TestUtil.WaitFor(() => ra.MemberStates.Count == 1 && rb.MemberStates.Count == 1);

        var path = MakeFile("pick.bin", 1_300_000);
        var offer = await a.ShareFileAsync(path, room: ra);
        var got = new TaskCompletionSource<ChatMessage>();
        rb.MessageReceived += m => { if (m.File != null) got.TrySetResult(m); };
        await ra.SendAsync(offer.ToText(""));
        var message = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));

        b.StartFileDownload(message, place);
        await TestUtil.WaitFor(() => b.HasFile(offer.Root), timeoutMs: 30_000);
        Assert.Equal(place, b.FilePath(offer.Root));
        Assert.Equal(File.ReadAllBytes(path), chosen.ToArray());
    }

    [Fact]
    public async Task A_shared_file_and_a_download_under_way_are_remembered_across_a_restart()
    {
        var dir = Path.Combine(_dir, "library");
        var path = MakeFile("lib.bin", 1_500_000);
        var (offer, hashes) = await PieceHashes.HashFileAsync(path);
        var other = new FileOffer(new string('c', 64), "other.bin", 9_000_000, FileOffer.DefaultPieceSize);

        var library = new FileLibrary(dir);
        library.AddScope(offer.Root, "Room one", null);
        library.AddShared(path, offer, hashes);
        library.AddScope(other.Root, null, new string('d', 64));
        library.SetDownload(other, Path.Combine(_dir, "where.bin"), paused: true);

        var again = new FileLibrary(dir);
        Assert.NotNull(again.Shared(offer.Root));
        Assert.Contains("Room one", again.ScopeOf(offer.Root).Rooms);
        var record = Assert.Single(again.Downloads());
        Assert.Equal(other, record.Offer);
        Assert.True(record.Paused);
        Assert.Contains(new string('d', 64), again.ScopeOf(other.Root).Accounts);

        // A file that moved or changed is not offered after a restart.
        File.WriteAllBytes(path, new byte[10]);
        Assert.Null(new FileLibrary(dir).Shared(offer.Root));
    }
}
