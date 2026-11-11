using System.Net;
using System.Text;
using System.Text.Json;
using Yaparr.AppCore;
using Yaparr.Identity;

namespace Yaparr.Tests;

public sealed class AutoBackupTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-auto-" + Guid.NewGuid().ToString("N"));
    private const string Code = "K7QD-M3XF-9PLR-2WTE-HV8N-C4YB";

    public AutoBackupTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    // ---- when a backup is due ----

    private static BackupServerState Ready(Action<BackupServerState>? change = null)
    {
        var state = new BackupServerState
        {
            Url = "https://cloud.yaparr.com", Username = "sam@example.com", Token = "ycl_x", RecoveryCode = Code, Version = 3,
            AutoBackup = true, LastBackup = T0.AddHours(-1), KeysFingerprint = "keys-1",
        };
        change?.Invoke(state);
        return state;
    }

    private static AutoBackupWhy Decide(BackupServerState s, string keys = "keys-1", DateTimeOffset? since = null, bool history = false, DateTimeOffset? now = null) =>
        AutoBackupPlan.Decide(s, keys, since, () => history, now ?? T0);

    [Fact]
    public void Nothing_is_sent_unless_it_is_on_and_there_is_a_first_backup_made_by_hand()
    {
        Assert.Equal(AutoBackupWhy.Nothing, Decide(Ready(s => s.AutoBackup = false), keys: "changed", since: T0.AddHours(-1), history: true));
        Assert.Equal(AutoBackupWhy.Nothing, Decide(Ready(s => s.Token = ""), keys: "changed", since: T0.AddHours(-1)));
        Assert.Equal(AutoBackupWhy.Nothing, Decide(Ready(s => s.RecoveryCode = ""), keys: "changed", since: T0.AddHours(-1)));
        Assert.Equal(AutoBackupWhy.Nothing, Decide(Ready(s => s.Version = 0), keys: "changed", since: T0.AddHours(-1)));   // no backup yet: no code to lock with
        Assert.Equal(AutoBackupWhy.KeysChanged, Decide(Ready(), keys: "changed", since: T0.AddHours(-1)));                // and with all of it, it is due
    }

    [Fact]
    public void A_change_to_what_cannot_be_lost_is_saved_once_it_has_been_still_for_a_moment()
    {
        Assert.Equal(AutoBackupWhy.Nothing, Decide(Ready(), keys: "changed", since: null));                                       // not seen yet
        Assert.Equal(AutoBackupWhy.Nothing, Decide(Ready(), keys: "changed", since: T0.AddSeconds(-30)));                         // still settling
        Assert.Equal(AutoBackupWhy.Nothing, Decide(Ready(), keys: "changed", since: T0 - AutoBackupPlan.KeysQuietFor + TimeSpan.FromSeconds(1)));
        Assert.Equal(AutoBackupWhy.KeysChanged, Decide(Ready(), keys: "changed", since: T0 - AutoBackupPlan.KeysQuietFor));       // two minutes
        Assert.Equal(AutoBackupWhy.KeysChanged, Decide(Ready(s => s.LastBackup = T0.AddMinutes(-3)), keys: "changed", since: T0.AddMinutes(-10)));   // however recent the last backup
    }

    [Fact]
    public void The_history_is_saved_once_a_day_and_only_if_it_changed()
    {
        Assert.Equal(AutoBackupWhy.Nothing, Decide(Ready(s => s.LastBackup = T0.AddHours(-23)), history: true));
        Assert.Equal(AutoBackupWhy.Daily, Decide(Ready(s => s.LastBackup = T0.AddHours(-24)), history: true));
        Assert.Equal(AutoBackupWhy.Daily, Decide(Ready(s => s.LastBackup = T0.AddDays(-9)), history: true));
        Assert.Equal(AutoBackupWhy.Nothing, Decide(Ready(s => s.LastBackup = T0.AddDays(-9)), history: false));    // nothing new to save
        Assert.Equal(AutoBackupWhy.Daily, Decide(Ready(s => s.LastBackup = null), history: true));                 // a backup from before it was recorded counts as long ago
    }

    [Fact]
    public void The_history_is_only_looked_at_when_a_day_has_passed()
    {
        var asked = 0;
        Assert.Equal(AutoBackupWhy.Nothing, AutoBackupPlan.Decide(Ready(), "keys-1", null, () => { asked++; return true; }, T0));
        Assert.Equal(0, asked);     // the history folder is not walked every few minutes
    }

    [Fact]
    public void It_waits_while_paused_or_backing_off()
    {
        Assert.Equal(AutoBackupWhy.Nothing, Decide(Ready(s => s.AutoPaused = true), keys: "changed", since: T0.AddHours(-1), history: true));
        Assert.Equal(AutoBackupWhy.Nothing, Decide(Ready(s => s.RetryAfter = T0.AddMinutes(1)), keys: "changed", since: T0.AddHours(-1)));
        Assert.Equal(AutoBackupWhy.KeysChanged, Decide(Ready(s => s.RetryAfter = T0.AddMinutes(-1)), keys: "changed", since: T0.AddHours(-1)));
    }

    [Fact]
    public void The_wait_after_a_failure_doubles_up_to_six_hours()
    {
        Assert.Equal(new[] { 5, 10, 20, 40, 80, 160, 320, 360, 360 }, Enumerable.Range(1, 9).Select(n => (int)AutoBackupPlan.Backoff(n).TotalMinutes).ToArray());
        Assert.Equal(TimeSpan.FromMinutes(5), AutoBackupPlan.Backoff(0));
        Assert.Equal(TimeSpan.FromMinutes(360), AutoBackupPlan.Backoff(500));
    }

    // ---- what the fingerprint notices ----

    private AppData NewProfile(string name = "profile")
    {
        var data = new AppData(Path.Combine(_dir, name));
        using (var identity = PeerIdentity.Generate()) data.SaveIdentity(identity);
        data.SaveSettings(new AppSettings { DisplayName = "Zed" });
        return data;
    }

    [Fact]
    public void The_fingerprint_changes_with_a_room_the_name_or_the_identity_and_not_with_a_setting_or_with_messages()
    {
        var data = NewProfile();
        var start = data.KeysFingerprint();
        Assert.Equal(start, data.KeysFingerprint());                                                // stable

        var settings = data.LoadSettings();
        settings.Theme = settings.Theme == "Dark" ? "Light" : "Dark";
        settings.VoiceOutputVolume = 33;
        data.SaveSettings(settings);
        Assert.Equal(start, data.KeysFingerprint());                                                // a setting does not count

        Directory.CreateDirectory(Path.Combine(data.Directory, "history"));
        File.WriteAllText(Path.Combine(data.Directory, "history", "room.log"), "a message");
        File.WriteAllText(Path.Combine(data.Directory, "peers.json"), "[]");
        Assert.Equal(start, data.KeysFingerprint());                                                // nor do messages, nor who was seen

        settings.PrivateRooms.Add(new PrivateRoomSetting { Label = "Friends", Key = "room-key-1" });
        data.SaveSettings(settings);
        var withRoom = data.KeysFingerprint();
        Assert.NotEqual(start, withRoom);                                                            // a room does

        settings.PrivateRooms[0].Key = "room-key-2";                                                 // a key that changed does
        data.SaveSettings(settings);
        Assert.NotEqual(withRoom, data.KeysFingerprint());

        settings.DisplayName = "Zed Two";                                                            // and so does the name
        data.SaveSettings(settings);
        var named = data.KeysFingerprint();
        Assert.NotEqual(withRoom, named);

        using (var other = PeerIdentity.Generate()) data.SaveIdentity(other);                       // and the identity
        Assert.NotEqual(named, data.KeysFingerprint());
    }

    [Fact]
    public void The_fingerprint_is_the_same_for_the_same_content_even_if_the_files_were_written_again()
    {
        var data = NewProfile();
        var first = data.KeysFingerprint();
        data.SaveSettings(data.LoadSettings());              // written again: the sealed bytes on disk differ, what is inside does not
        Assert.Equal(first, data.KeysFingerprint());
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void A_changed_history_is_noticed_by_the_dates_of_its_files()
    {
        var data = NewProfile();
        Assert.False(data.HistoryChangedSince(DateTimeOffset.MinValue));                           // no history yet
        Directory.CreateDirectory(Path.Combine(data.Directory, "history"));
        var log = Path.Combine(data.Directory, "history", "room.log");
        File.WriteAllText(log, "hello");
        File.SetLastWriteTimeUtc(log, T0.UtcDateTime);

        Assert.True(data.HistoryChangedSince(T0.AddMinutes(-1)));
        Assert.False(data.HistoryChangedSince(T0.AddMinutes(1)));
        Directory.CreateDirectory(Path.Combine(data.Directory, "direct"));
        var chat = Path.Combine(data.Directory, "direct", "chat.log");
        File.WriteAllText(chat, "hi");
        File.SetLastWriteTimeUtc(chat, T0.AddHours(1).UtcDateTime);                               // a direct chat, later
        Assert.True(data.HistoryChangedSince(T0.AddMinutes(1)));
        Assert.False(data.HistoryChangedSince(T0.AddHours(2)));
    }

    // ---- doing it, against a server ----

    private sealed class FakeServer : HttpMessageHandler
    {
        public readonly List<byte[]> Versions = new();
        public int Puts, Gets;
        public bool Down, Unauthorized;
        public string Token = "ycl_x";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            if (Down) throw new HttpRequestException("connection refused");
            var asked = request.Headers.Authorization?.Parameter;
            if (Unauthorized || asked != Token) return Json(HttpStatusCode.Unauthorized, new { error = "no" });
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/v1/backup/versions") { Gets++; return Json(HttpStatusCode.OK, new { username = "sam@example.com", versions = Versions.Select((v, i) => new { version = i + 1, size = v.Length, at = DateTimeOffset.UtcNow }).Reverse() }); }
            if (path == "/v1/backup" && request.Method == HttpMethod.Put)
            {
                var basedOn = int.Parse(request.Headers.IfMatch.Single().Tag.Trim('"'));
                if (basedOn != Versions.Count) return Json(HttpStatusCode.Conflict, new { error = "newer", version = Versions.Count });
                Versions.Add(await request.Content!.ReadAsByteArrayAsync(cancel));
                Puts++;
                return Json(HttpStatusCode.Created, new { version = Versions.Count, size = Versions[^1].Length });
            }
            return Json(HttpStatusCode.NotFound, new { error = "?" });
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };
    }

    // A profile that has made its first backup by hand (version 1, now at T0) and has automatic backups on.
    private (AppData Data, FakeServer Server, AutoBackup Auto, Func<DateTimeOffset> Clock, Action<TimeSpan> Advance) Setup(Action<BackupServerState>? change = null)
    {
        var data = NewProfile();
        var server = new FakeServer();
        server.Versions.Add(new byte[] { 1 });                                  // the backup made by hand
        var now = T0;
        var state = new BackupServerState
        {
            Url = "https://cloud.yaparr.com", Username = "sam@example.com", Token = "ycl_x", RecoveryCode = Code, AutoBackup = true,
        };
        state.RecordBackup(1, data.KeysFingerprint(), T0);
        change?.Invoke(state);
        data.SaveBackupServer(state);
        var auto = new AutoBackup(data, () => now, new HttpClient(server));
        return (data, server, auto, () => now, d => now += d);
    }

    private static void ChangeARoom(AppData data, string key = "room-key-1")
    {
        var settings = data.LoadSettings();
        settings.PrivateRooms.Add(new PrivateRoomSetting { Label = "Friends", Key = key });
        data.SaveSettings(settings);
    }

    [Fact]
    public async Task With_nothing_changed_nothing_is_sent_and_the_server_is_not_even_asked()
    {
        var (_, server, auto, _, advance) = Setup();
        advance(TimeSpan.FromMinutes(30));
        Assert.Equal(AutoBackupResult.Nothing, await auto.CheckAsync());
        Assert.Equal(0, server.Gets + server.Puts);
    }

    [Fact]
    public async Task A_new_room_is_backed_up_after_it_has_been_still_for_two_minutes_and_the_backup_is_locked_with_the_code()
    {
        var (data, server, auto, _, advance) = Setup();
        ChangeARoom(data);

        Assert.Equal(AutoBackupResult.Nothing, await auto.CheckAsync());          // seen; the quiet time starts
        advance(TimeSpan.FromMinutes(1));
        Assert.Equal(AutoBackupResult.Nothing, await auto.CheckAsync());          // not yet
        advance(TimeSpan.FromMinutes(1.5));
        var changed = 0;
        auto.Changed += () => changed++;
        Assert.Equal(AutoBackupResult.BackedUp, await auto.CheckAsync());
        Assert.Equal(1, changed);

        Assert.Equal(2, server.Versions.Count);
        var file = Path.Combine(_dir, "downloaded.yapbak");
        File.WriteAllBytes(file, server.Versions[1]);
        Assert.True(AppData.IsBackupLocked(file));
        Assert.True(BackupServerClient.CodeOpens(file, Code));                   // locked with the recovery code
        Assert.False(BackupServerClient.CodeOpens(file, RecoveryCode.Generate()));

        var state = data.LoadBackupServer();
        Assert.Equal(2, state.Version);
        Assert.Equal(T0.AddMinutes(2.5), state.LastBackup);
        Assert.Equal(data.KeysFingerprint(), state.KeysFingerprint);
        Assert.Contains("version 2", state.AutoNote);
        Assert.Contains("rooms or keys changed", state.AutoNote);

        Assert.Equal(AutoBackupResult.Nothing, await auto.CheckAsync());          // and it is not sent again
        Assert.Equal(1, server.Puts);
    }

    [Fact]
    public async Task A_burst_of_changes_becomes_one_backup_because_each_change_starts_the_quiet_time_again()
    {
        var (data, server, auto, _, advance) = Setup();
        ChangeARoom(data, "a");
        await auto.CheckAsync();
        advance(TimeSpan.FromMinutes(1.5));
        ChangeARoom(data, "b");                                                  // another room, before the first had settled
        await auto.CheckAsync();
        advance(TimeSpan.FromMinutes(1.5));
        Assert.Equal(AutoBackupResult.Nothing, await auto.CheckAsync());          // three minutes since the first, but only 1.5 since the last
        advance(TimeSpan.FromMinutes(1));
        Assert.Equal(AutoBackupResult.BackedUp, await auto.CheckAsync());
        Assert.Equal(1, server.Puts);
        Assert.Equal(data.KeysFingerprint(), data.LoadBackupServer().KeysFingerprint);          // with both rooms
    }

    [Fact]
    public async Task A_change_that_is_undone_before_it_settles_is_not_saved()
    {
        var (data, server, auto, _, advance) = Setup();
        var before = data.LoadSettings();
        ChangeARoom(data);
        await auto.CheckAsync();
        data.SaveSettings(before);                                               // the room is gone again
        advance(TimeSpan.FromMinutes(5));
        Assert.Equal(AutoBackupResult.Nothing, await auto.CheckAsync());
        Assert.Equal(0, server.Puts);
    }

    [Fact]
    public async Task The_history_is_backed_up_when_a_day_has_passed_and_not_before_and_not_if_it_did_not_change()
    {
        var (data, server, auto, _, advance) = Setup();
        Directory.CreateDirectory(Path.Combine(data.Directory, "history"));
        var log = Path.Combine(data.Directory, "history", "room.log");

        advance(TimeSpan.FromHours(20));
        File.WriteAllText(log, "messages");
        File.SetLastWriteTimeUtc(log, T0.AddHours(19).UtcDateTime);
        Assert.Equal(AutoBackupResult.Nothing, await auto.CheckAsync());          // new messages, but not a day yet

        advance(TimeSpan.FromHours(5));
        Assert.Equal(AutoBackupResult.BackedUp, await auto.CheckAsync());         // a day, and there are new messages
        Assert.Contains("version 2)", data.LoadBackupServer().AutoNote);
        Assert.DoesNotContain("rooms or keys", data.LoadBackupServer().AutoNote);

        advance(TimeSpan.FromHours(30));
        Assert.Equal(AutoBackupResult.Nothing, await auto.CheckAsync());          // another day, but no new messages
        Assert.Equal(1, server.Puts);
    }

    [Fact]
    public async Task Another_device_having_saved_pauses_it_without_sending_anything_and_a_backup_by_hand_carries_on()
    {
        var (data, server, auto, _, advance) = Setup();
        server.Versions.Add(new byte[] { 2 });                                   // another device saved version 2
        ChangeARoom(data);
        await auto.CheckAsync();
        advance(TimeSpan.FromMinutes(3));

        Assert.Equal(AutoBackupResult.Paused, await auto.CheckAsync());
        Assert.Equal(0, server.Puts);
        var state = data.LoadBackupServer();
        Assert.True(state.AutoPaused);
        Assert.Contains("another device saved a newer backup (version 2)", state.AutoNote);
        Assert.True(state.AutoBackup);                                           // still on: it waits

        // Waiting does not ask the server again every few minutes.
        var asked = server.Gets;
        advance(TimeSpan.FromHours(30));
        Assert.Equal(AutoBackupResult.Nothing, await auto.CheckAsync());
        Assert.Equal(asked, server.Gets);

        // The person backed up by hand, which asked them first, and recorded it.
        state.RecordBackup(3, data.KeysFingerprint(), T0.AddHours(40));
        data.SaveBackupServer(state);
        Assert.False(data.LoadBackupServer().AutoPaused);
    }

    [Fact]
    public async Task A_backup_that_loses_a_race_to_another_device_pauses_too()
    {
        var (data, server, auto, _, advance) = Setup();
        ChangeARoom(data);
        await auto.CheckAsync();
        advance(TimeSpan.FromMinutes(3));
        // The server answers the version list with one, and by the time the save arrives another device has put a second.
        var racing = new RacingServer(server);
        var now = T0;
        var raced = new AutoBackup(data, () => now, new HttpClient(racing));
        await raced.CheckAsync();                                                // seen; the quiet time starts
        now += TimeSpan.FromMinutes(3);
        Assert.Equal(AutoBackupResult.Paused, await raced.CheckAsync());
        Assert.True(data.LoadBackupServer().AutoPaused);
    }

    private sealed class RacingServer(FakeServer inner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            if (request.Method == HttpMethod.Put) inner.Versions.Add(new byte[] { 9 });    // someone else got in first
            return await new HttpMessageInvoker(inner).SendAsync(request, cancel);
        }
    }

    [Fact]
    public async Task A_server_that_no_longer_knows_this_device_stops_it_and_says_so()
    {
        var (data, server, auto, _, advance) = Setup();
        ChangeARoom(data);
        await auto.CheckAsync();
        advance(TimeSpan.FromMinutes(3));
        server.Unauthorized = true;

        Assert.Equal(AutoBackupResult.SignedOut, await auto.CheckAsync());
        var state = data.LoadBackupServer();
        Assert.False(state.SignedIn);
        Assert.Equal("", state.Token);
        Assert.Contains("no longer signed in", state.AutoNote);
        Assert.Equal(AutoBackupResult.Nothing, await auto.CheckAsync());          // signed out: nothing more until the person signs in
    }

    [Fact]
    public async Task A_failure_backs_off_and_then_tries_again_and_a_success_clears_it()
    {
        var (data, server, auto, _, advance) = Setup();
        ChangeARoom(data);
        await auto.CheckAsync();
        advance(TimeSpan.FromMinutes(3));
        server.Down = true;

        Assert.Equal(AutoBackupResult.Failed, await auto.CheckAsync());
        var state = data.LoadBackupServer();
        Assert.Equal(1, state.Failures);
        Assert.Equal(T0.AddMinutes(3) + TimeSpan.FromMinutes(5), state.RetryAfter);
        Assert.Contains("Couldn't reach the backup server", state.AutoNote);
        Assert.Contains("Trying again later", state.AutoNote);
        Assert.True(state.AutoBackup);

        advance(TimeSpan.FromMinutes(4));
        var gets = server.Gets;
        Assert.Equal(AutoBackupResult.Nothing, await auto.CheckAsync());          // waiting out the five minutes
        advance(TimeSpan.FromMinutes(2));
        Assert.Equal(AutoBackupResult.Failed, await auto.CheckAsync());           // down still: the second failure waits ten
        Assert.Equal(2, data.LoadBackupServer().Failures);
        Assert.Equal(T0.AddMinutes(9) + TimeSpan.FromMinutes(10), data.LoadBackupServer().RetryAfter);

        server.Down = false;
        advance(TimeSpan.FromMinutes(11));
        Assert.Equal(AutoBackupResult.BackedUp, await auto.CheckAsync());
        var after = data.LoadBackupServer();
        Assert.Equal(0, after.Failures);
        Assert.Null(after.RetryAfter);
        Assert.Null(after.FirstFailure);
        _ = gets;
    }

    [Fact]
    public async Task A_long_run_of_failures_is_said_plainly()
    {
        var (data, server, auto, _, advance) = Setup(s => { s.FirstFailure = T0.AddDays(-4); s.Failures = 12; });
        ChangeARoom(data);
        await auto.CheckAsync();
        advance(TimeSpan.FromMinutes(3));
        server.Down = true;
        await auto.CheckAsync();
        Assert.Contains("failing for 4 days", data.LoadBackupServer().AutoNote);
    }

    [Fact]
    public async Task While_a_backup_is_running_a_check_stands_aside()
    {
        var (data, server, auto, _, advance) = Setup();
        ChangeARoom(data);
        await auto.CheckAsync();
        advance(TimeSpan.FromMinutes(3));
        await auto.Gate.WaitAsync();                                              // a backup the person asked for is under way
        try { Assert.Equal(AutoBackupResult.Busy, await auto.CheckAsync()); }
        finally { auto.Gate.Release(); }
        Assert.Equal(0, server.Puts);
        Assert.Equal(AutoBackupResult.BackedUp, await auto.CheckAsync());         // and it goes ahead afterwards
    }

    [Fact]
    public async Task Turned_off_or_signed_out_it_does_nothing_at_all()
    {
        var (data, server, auto, _, advance) = Setup(s => s.AutoBackup = false);
        ChangeARoom(data);
        advance(TimeSpan.FromDays(3));
        Assert.Equal(AutoBackupResult.Nothing, await auto.CheckAsync());

        var state = data.LoadBackupServer();
        state.AutoBackup = true;
        state.Token = "";
        data.SaveBackupServer(state);
        Assert.Equal(AutoBackupResult.Nothing, await auto.CheckAsync());
        Assert.Equal(0, server.Gets + server.Puts);
    }

    [Fact]
    public void What_automatic_backups_remember_survives_a_restart_and_older_files_still_load()
    {
        var data = NewProfile();
        var state = Ready(s => { s.AutoNote = "a note"; s.Failures = 2; s.FirstFailure = T0; s.RetryAfter = T0.AddMinutes(10); s.AutoPaused = true; });
        data.SaveBackupServer(state);
        var loaded = data.LoadBackupServer();
        Assert.True(loaded.AutoBackup);
        Assert.True(loaded.AutoPaused);
        Assert.Equal("a note", loaded.AutoNote);
        Assert.Equal(2, loaded.Failures);
        Assert.Equal(T0.AddMinutes(10), loaded.RetryAfter);
        Assert.Equal("keys-1", loaded.KeysFingerprint);

        // A file written before automatic backups existed has none of this: everything is off.
        System.IO.File.Delete(Path.Combine(data.Directory, "backup-server.json"));
        data.SaveBackupServer(new BackupServerState { Url = "https://cloud.yaparr.com", Token = "ycl_x", RecoveryCode = Code, Version = 4 });
        var old = data.LoadBackupServer();
        Assert.False(old.AutoBackup);
        Assert.Null(old.LastBackup);
        Assert.Equal("", old.KeysFingerprint);
    }
}
