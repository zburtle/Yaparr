using System.Security.Cryptography;
using System.Text;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

/// <summary>A room's list of sounds is sealed on disk with the history key, like the history and the sound files (issue #33).</summary>
public class SoundboardSealingTests
{
    private static float[] Tone() => Enumerable.Range(0, 48000).Select(i => (float)(0.4 * Math.Sin(2 * Math.PI * 440 * i / 48000))).ToArray();

    private static HistoryCipher NewKey() => new(RandomNumberGenerator.GetBytes(32));

    private static ChatNode Node(string lists, string files, HistoryCipher? cipher) =>
        TestUtil.NewNode(new InMemoryDiscoveryHub(), "alice", soundboardDirectory: lists, attachmentDirectory: files, historyCipher: cipher);

    private static string ListFile(string lists) => Directory.GetFiles(lists, "*.sounds.json").Single();

    private static (string Root, string Lists, string Files) Folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "yaparr-sbseal-" + Guid.NewGuid().ToString("N"));
        return (root, Path.Combine(root, "lists"), Path.Combine(root, "files"));
    }

    [Fact]
    public async Task The_list_on_disk_does_not_show_the_names_and_a_restart_reads_it_back()
    {
        var (root, lists, files) = Folders();
        var key = NewKey();
        try
        {
            using (var first = Node(lists, files, key))
            {
                var room = await first.JoinRoomAsync("lobby");
                room.AddSound("airhorn", SoundClip.FromPcm(Tone()));
            }

            var bytes = File.ReadAllBytes(ListFile(lists));
            Assert.True(HistoryCipher.IsSealedFile(bytes.AsSpan(0, HistoryCipher.SealedFileMagicLength)));
            Assert.DoesNotContain("airhorn", Encoding.UTF8.GetString(bytes));
            Assert.DoesNotContain("alice", Encoding.UTF8.GetString(bytes));   // who added it is hidden too

            using var second = Node(lists, files, key);
            var again = await second.JoinRoomAsync("lobby");
            Assert.Equal("airhorn", Assert.Single(again.Sounds).Name);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task A_plain_list_from_an_earlier_version_is_read_and_written_back_sealed()
    {
        var (root, lists, files) = Folders();
        try
        {
            using (var old = Node(lists, files, cipher: null))
            {
                var room = await old.JoinRoomAsync("lobby");
                room.AddSound("airhorn", SoundClip.FromPcm(Tone()));
            }
            Assert.Contains("airhorn", File.ReadAllText(ListFile(lists)));   // plain, as before

            using var now = Node(lists, files, NewKey());
            var again = await now.JoinRoomAsync("lobby");
            Assert.Equal("airhorn", Assert.Single(again.Sounds).Name);
            var bytes = File.ReadAllBytes(ListFile(lists));
            Assert.True(HistoryCipher.IsSealedFile(bytes.AsSpan(0, HistoryCipher.SealedFileMagicLength)), "it was rewritten sealed");
            Assert.DoesNotContain("airhorn", Encoding.UTF8.GetString(bytes));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task A_list_sealed_with_another_key_is_kept_aside_and_the_app_starts_with_an_empty_one()
    {
        var (root, lists, files) = Folders();
        try
        {
            using (var first = Node(lists, files, NewKey()))
            {
                var room = await first.JoinRoomAsync("lobby");
                room.AddSound("airhorn", SoundClip.FromPcm(Tone()));
            }
            var before = File.ReadAllBytes(ListFile(lists));

            using var second = Node(lists, files, NewKey());
            var again = await second.JoinRoomAsync("lobby");
            Assert.Empty(again.Sounds);
            // Not written over: the file is still there under another name.
            var aside = Directory.GetFiles(lists, "*.unreadable").Single();
            Assert.Equal(before, File.ReadAllBytes(aside));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task A_sealed_list_cannot_be_passed_off_as_another_rooms_by_swapping_the_files()
    {
        var (root, lists, files) = Folders();
        var key = NewKey();
        try
        {
            string lobbyFile, otherFile;
            using (var first = Node(lists, files, key))
            {
                var lobby = await first.JoinRoomAsync("lobby");
                var other = await first.JoinRoomAsync("other");
                lobby.AddSound("airhorn", SoundClip.FromPcm(Tone()));
                other.AddSound("bell", SoundClip.FromPcm(Tone()));
                lobbyFile = Path.Combine(lists, lobby.Key + ".sounds.json");
                otherFile = Path.Combine(lists, other.Key + ".sounds.json");
            }
            Assert.True(File.Exists(lobbyFile) && File.Exists(otherFile));

            // Each file is tied to its own name: one room's list under the other's name does not open, so nobody can be handed a list that is not the room's.
            var swap = lobbyFile + ".swap";
            File.Move(lobbyFile, swap);
            File.Move(otherFile, lobbyFile);
            File.Move(swap, otherFile);

            using var second = Node(lists, files, key);
            var again = await second.JoinRoomAsync("lobby");
            Assert.Empty(again.Sounds);
            Assert.Single(Directory.GetFiles(lists, "*.unreadable"));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
