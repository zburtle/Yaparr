using System.IO.Compression;
using System.Text;
using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.History.Sqlite;
using Yaparr.Identity;

namespace Yaparr.Tests;

// A backup is made while the app is running and writing to the history database: what goes into it has to be a whole database.
public class HistoryBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-historybackup-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Key = Enumerable.Range(11, 32).Select(i => (byte)i).ToArray();

    public HistoryBackupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static void Write(IChatHistoryStore store, PeerIdentity who, int count, int from = 0)
    {
        for (int i = from; i < from + count; i++)
        {
            var m = SignedMessage.Create(who, "room", "x", $"secret meeting number {i}", 1_000_000 + i);
            store.Add("room", SignedMessage.Key(m), m, null);
        }
    }

    private static int Count(string path, byte[] key)
    {
        using var store = new SqliteHistoryStore(path, key);
        return store.Newest("room", int.MaxValue).Count;
    }

    [Fact]
    public async Task A_snapshot_taken_while_messages_are_being_written_is_a_whole_database_with_the_same_key()
    {
        using var who = PeerIdentity.Generate();
        using var store = new SqliteHistoryStore(Path.Combine(_dir, "live.db"), Key);
        Write(store, who, 500);
        using var stop = new CancellationTokenSource();
        int written = 500;
        var writer = Task.Run(() => { while (!stop.IsCancellationRequested) { Write(store, who, 20, written); written += 20; } });

        for (int round = 0; round < 6; round++)
        {
            var path = Path.Combine(_dir, $"snap{round}.db");
            store.SnapshotTo(path, Key);
            Assert.True(Count(path, Key) >= 500);
            Assert.DoesNotContain("secret meeting", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
            await Task.Delay(30);
        }
        stop.Cancel();
        await writer;
    }

    [Fact]
    public async Task A_profile_backup_made_during_writes_restores_into_a_database_that_opens()
    {
        var source = Path.Combine(_dir, "profile");
        var data = new AppData(source);
        using var identity = PeerIdentity.Generate();
        data.SaveIdentity(identity);
        data.SaveSettings(new AppSettings { DisplayName = "Zed" });
        var cipher = HistoryCipher.LoadOrCreate(Path.Combine(source, "history.key"));
        using var store = data.OpenHistoryStore(cipher);
        Write(store, identity, 300);
        // Something the database leaves beside itself while it writes; a backup must not take it along.
        File.WriteAllText(Path.Combine(data.HistoryDirectory, "history.db-journal"), "half a journal");

        using var stop = new CancellationTokenSource();
        int written = 300;
        var writer = Task.Run(() => { while (!stop.IsCancellationRequested) { Write(store, identity, 10, written); written += 10; } });

        var zip = Path.Combine(_dir, "profile.zip");
        for (int round = 0; round < 4; round++)
        {
            await Task.Run(() => data.ExportProfile(zip));
            using (var archive = ZipFile.OpenRead(zip))
            {
                Assert.Contains(archive.Entries, e => e.FullName == "history/history.db");
                Assert.DoesNotContain(archive.Entries, e => e.FullName.Contains("-journal") || e.FullName.Contains("snapshot"));
            }
            Assert.False(File.Exists(Path.Combine(source, "history.snapshot.tmp")));

            var staging = new AppData(Path.Combine(_dir, "restored" + round)).StageProfile(zip);
            try
            {
                var restoredKey = new HistoryCipher(File.ReadAllBytes(Path.Combine(staging, "history.key"))).DatabaseKey();
                Assert.True(Count(Path.Combine(staging, "history", "history.db"), restoredKey) >= 300);
            }
            finally { Directory.Delete(staging, true); }
        }
        stop.Cancel();
        await writer;
    }

    [Fact]
    public void Pictures_that_have_been_here_more_than_six_months_are_left_out_of_a_backup()
    {
        var data = new AppData(Path.Combine(_dir, "pictures"));
        using var identity = PeerIdentity.Generate();
        data.SaveIdentity(identity);
        var folder = Path.Combine(data.Directory, "attachments");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "recent"), new byte[10]);
        File.WriteAllBytes(Path.Combine(folder, "almost"), new byte[10]);
        File.WriteAllBytes(Path.Combine(folder, "old"), new byte[10]);
        File.SetLastWriteTimeUtc(Path.Combine(folder, "almost"), DateTime.UtcNow - AppData.BackupAttachmentAge + TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(Path.Combine(folder, "old"), DateTime.UtcNow - AppData.BackupAttachmentAge - TimeSpan.FromDays(2));

        var zip = Path.Combine(_dir, "pictures.zip");
        data.ExportProfile(zip);
        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("attachments/recent", names);
        Assert.Contains("attachments/almost", names);
        Assert.DoesNotContain("attachments/old", names);
        Assert.Contains("identity.key", names);
    }
}
