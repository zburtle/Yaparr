using System.Security.Cryptography;
using System.Text;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class DeviceIdentityTests
{
    [Fact]
    public void A_linked_device_has_a_key_of_its_own_and_a_certificate_for_the_account()
    {
        using var first = PeerIdentity.Generate();
        using var second = first.CreateDevice();

        Assert.Equal(first.Id, first.AccountId);
        Assert.Null(first.DeviceCertificate);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.AccountId, second.AccountId);
        Assert.True(PeerIdentity.VerifyDevice(second.AccountPublicKey, second.PublicKey, second.DeviceCertificate!));
        Assert.True(second.CanLinkDevices);
    }

    [Fact]
    public void A_certificate_belongs_to_one_device_and_one_account()
    {
        using var first = PeerIdentity.Generate();
        using var second = first.CreateDevice();
        using var third = first.CreateDevice();
        using var stranger = PeerIdentity.Generate();

        Assert.False(PeerIdentity.VerifyDevice(first.AccountPublicKey, third.PublicKey, second.DeviceCertificate!));
        Assert.False(PeerIdentity.VerifyDevice(stranger.AccountPublicKey, second.PublicKey, second.DeviceCertificate!));
        Assert.False(PeerIdentity.VerifyDevice(first.AccountPublicKey, second.PublicKey, Array.Empty<byte>()));
    }

    [Fact]
    public void A_saved_identity_comes_back_as_it_was_and_the_first_device_is_still_a_bare_key()
    {
        using var first = PeerIdentity.Generate();
        using var second = first.CreateDevice();

        var bare = first.Export();
        Assert.Equal(first.ExportPrivateKey(), bare);   // readable by earlier versions
        using var restoredFirst = PeerIdentity.Import(bare);
        Assert.Equal(first.Id, restoredFirst.Id);
        Assert.Equal(first.AccountId, restoredFirst.AccountId);

        using var restored = PeerIdentity.Import(second.Export());
        Assert.Equal(second.Id, restored.Id);
        Assert.Equal(first.AccountId, restored.AccountId);
        Assert.True(restored.CanLinkDevices);
        using var third = restored.CreateDevice();
        Assert.Equal(first.AccountId, third.AccountId);
    }

    [Fact]
    public void A_saved_device_whose_certificate_does_not_fit_is_refused()
    {
        using var first = PeerIdentity.Generate();
        using var other = PeerIdentity.Generate();
        using var second = first.CreateDevice();
        using var forged = other.CreateDevice();

        // The device from one account, with the certificate that another account made for its own device.
        var export = second.Export();
        var wrongCertificate = forged.DeviceCertificate!;
        var tail = export.Length - second.DeviceCertificate!.Length;
        var tampered = export[..tail].Concat(wrongCertificate.Take(second.DeviceCertificate.Length)).ToArray();
        Assert.ThrowsAny<Exception>(() => PeerIdentity.Import(tampered));
    }
}

public class MultiDeviceTests
{
    private sealed class Devices : IDisposable
    {
        public readonly InMemoryDiscoveryHub Hub = new();
        public readonly PeerIdentity FirstId = PeerIdentity.Generate();
        public readonly PeerIdentity SecondId;
        public ChatNode First = null!, Second = null!, Other = null!;
        public ChatRoom RoomFirst = null!, RoomSecond = null!, RoomOther = null!;

        public Devices() => SecondId = FirstId.CreateDevice();

        public static async Task<Devices> StartAsync(string room = "lobby")
        {
            var d = new Devices();
            d.First = TestUtil.NewNode(d.Hub, "alice", identity: d.FirstId);
            d.Second = TestUtil.NewNode(d.Hub, "alice", identity: d.SecondId);
            d.Other = TestUtil.NewNode(d.Hub, "bob");
            d.RoomFirst = await d.First.JoinRoomAsync(room);
            d.RoomSecond = await d.Second.JoinRoomAsync(room);
            d.RoomOther = await d.Other.JoinRoomAsync(room);
            await TestUtil.WaitFor(() => d.RoomOther.Members.Count == 2 && d.RoomFirst.Members.Count == 2 && d.RoomSecond.Members.Count == 2);
            return d;
        }

        public void Dispose()
        {
            First.Dispose(); Second.Dispose(); Other.Dispose();
            FirstId.Dispose(); SecondId.Dispose();
        }
    }

    [Fact]
    public async Task Everyone_sees_both_devices_as_one_person_and_the_devices_find_each_other()
    {
        using var d = await Devices.StartAsync();

        Assert.Equal(d.First.AccountId, d.Second.AccountId);
        Assert.NotEqual(d.First.Id, d.Second.Id);

        var seenByOther = d.RoomOther.Members;
        Assert.Equal(2, seenByOther.Count);
        Assert.All(seenByOther, m => Assert.Equal(d.First.AccountId, m.Account));

        Assert.Contains(d.Second.MyOtherDevices, p => p.Id == d.First.Id);
        Assert.Contains(d.First.MyOtherDevices, p => p.Id == d.Second.Id);
        Assert.True(d.First.IsMyDevice(d.Second.Id));
        Assert.False(d.First.IsMyDevice(d.Other.Id));
    }

    [Fact]
    public async Task A_message_from_either_device_reaches_the_other_device_and_the_rest_of_the_room()
    {
        using var d = await Devices.StartAsync();

        var onFirst = new TaskCompletionSource<ChatMessage>();
        var onOther = new TaskCompletionSource<ChatMessage>();
        d.RoomFirst.MessageReceived += m => onFirst.TrySetResult(m);
        d.RoomOther.MessageReceived += m => onOther.TrySetResult(m);
        await d.RoomSecond.SendAsync("from my phone");

        var atFirst = await onFirst.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("from my phone", atFirst.Text);
        Assert.Equal(d.Second.Id, atFirst.From.Id);
        Assert.True(d.First.IsMyDevice(atFirst.From.Id));
        Assert.Equal(d.First.AccountId, atFirst.From.Account);

        var atOther = await onOther.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(d.First.AccountId, atOther.From.Account);
    }

    [Fact]
    public async Task A_device_that_was_off_catches_up_on_the_room_from_the_other_one()
    {
        using var d = new Devices();
        d.First = TestUtil.NewNode(d.Hub, "alice", identity: d.FirstId);
        d.Other = TestUtil.NewNode(d.Hub, "bob");
        d.RoomFirst = await d.First.JoinRoomAsync("lobby");
        d.RoomOther = await d.Other.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => d.RoomFirst.Members.Count == 1);
        await d.RoomOther.SendAsync("sent while the second device was off");
        await TestUtil.WaitFor(() => d.RoomFirst.GetHistory().Any());

        d.Other.Dispose();
        d.Second = TestUtil.NewNode(d.Hub, "alice", identity: d.SecondId);
        d.RoomSecond = await d.Second.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => d.RoomSecond.GetHistory().Any(m => m.Text == "sent while the second device was off"));
        d.Other = TestUtil.NewNode(d.Hub, "unused");   // so Dispose has something to dispose
    }

    [Fact]
    public async Task A_direct_message_to_the_person_reaches_every_device_and_counts_as_delivered_when_all_confirm()
    {
        using var d = await Devices.StartAsync();

        var got = new List<string>();
        d.First.IncomingDirectChat += c => c.MessageReceived += m => { lock (got) got.Add("first:" + m.Text); };
        d.Second.IncomingDirectChat += c => c.MessageReceived += m => { lock (got) got.Add("second:" + m.Text); };

        var chat = await d.Other.OpenDirectChatAsync(d.First.AccountId);
        Assert.Equal(d.First.AccountId, chat.PeerId);
        DeliveryStatus? last = null;
        chat.DeliveryChanged += s => last = s;
        await chat.SendAsync("hello both");

        await TestUtil.WaitFor(() => { lock (got) return got.Count == 2; });
        lock (got) Assert.Equivalent(new[] { "first:hello both", "second:hello both" }, got);
        await TestUtil.WaitFor(() => last is { State: DeliveryState.Delivered, Delivered: 2, Total: 2 });
    }

    [Fact]
    public async Task Both_devices_land_in_one_direct_chat_on_the_other_side()
    {
        using var d = await Devices.StartAsync();

        var chats = new List<DirectChat>();
        d.Other.IncomingDirectChat += c => { lock (chats) chats.Add(c); };
        var received = new List<string>();
        var chat1 = await d.First.OpenDirectChatAsync(d.Other.AccountId);
        var chat2 = await d.Second.OpenDirectChatAsync(d.Other.AccountId);
        await chat1.SendAsync("one");
        await TestUtil.WaitFor(() => { lock (chats) return chats.Count == 1; });
        chats[0].MessageReceived += m => { lock (received) received.Add(m.Text); };
        await chat2.SendAsync("two");
        await TestUtil.WaitFor(() => { lock (received) return received.Contains("two"); });
        lock (chats) Assert.Single(chats);
        Assert.Equal(d.First.AccountId, chats[0].PeerId);
    }

    [Fact]
    public async Task Blocking_a_person_blocks_all_of_their_devices_whichever_id_you_use()
    {
        using var d = await Devices.StartAsync();

        d.Other.Block(d.Second.Id);

        Assert.True(d.Other.IsBlocked(d.First.Id));
        Assert.True(d.Other.IsBlocked(d.Second.Id));
        Assert.True(d.Other.IsBlocked(d.First.AccountId));
        Assert.Equal(new[] { d.First.AccountId }, d.Other.BlockedPeers);
        await TestUtil.WaitFor(() => d.Other.Network.Get(d.First.Id) == null && d.Other.Network.Get(d.Second.Id) == null);

        d.Other.Unblock(d.First.Id);
        Assert.False(d.Other.IsBlocked(d.Second.Id));
        Assert.Empty(d.Other.BlockedPeers);
    }

    [Fact]
    public async Task You_cannot_block_your_own_other_device()
    {
        using var d = await Devices.StartAsync();
        await TestUtil.WaitFor(() => d.First.MyOtherDevices.Count == 1);
        Assert.Throws<ArgumentException>(() => d.First.Block(d.Second.Id));
        Assert.Throws<ArgumentException>(() => d.First.Block(d.First.AccountId));
    }

    [Fact]
    public async Task Renaming_on_one_device_renames_the_other()
    {
        using var d = await Devices.StartAsync();
        await TestUtil.WaitFor(() => d.First.MyOtherDevices.Count == 1 && d.Second.MyOtherDevices.Count == 1);
        string? announced = null;
        d.Second.DisplayNameChanged += n => announced = n;

        d.First.Rename("alice the great");

        await TestUtil.WaitFor(() => d.Second.DisplayName == "alice the great");
        Assert.Equal("alice the great", announced);
        await TestUtil.WaitFor(() => d.RoomOther.Members.Count(m => m.DisplayName == "alice the great") == 2);
    }

    [Fact]
    public async Task Blocking_on_one_device_blocks_on_the_other_and_unblocking_lifts_it()
    {
        using var d = await Devices.StartAsync();
        await TestUtil.WaitFor(() => d.First.MyOtherDevices.Count == 1 && d.Second.MyOtherDevices.Count == 1);

        d.First.Block(d.Other.Id);
        await TestUtil.WaitFor(() => d.Second.IsBlocked(d.Other.Id));
        d.Second.Unblock(d.Other.Id);
        await TestUtil.WaitFor(() => !d.First.IsBlocked(d.Other.Id));
    }

    [Fact]
    public async Task Joining_and_leaving_a_room_on_one_device_is_passed_to_the_other()
    {
        using var d = await Devices.StartAsync();
        await TestUtil.WaitFor(() => d.First.MyOtherDevices.Count == 1 && d.Second.MyOtherDevices.Count == 1);
        var events = new List<(SyncedRoom Room, bool Joined)>();
        // What an app does with the event: join the room, so a later removal has something to remove.
        d.Second.SyncedRoomChanged += (room, joined) =>
        {
            lock (events) events.Add((room, joined));
            if (joined) _ = d.Second.JoinPrivateRoomAsync(RoomKey.Parse(room.Key!), room.Name);
        };

        var key = RoomKey.Generate();
        var secret = await d.First.JoinPrivateRoomAsync(key, "book club");
        await TestUtil.WaitFor(() => { lock (events) return events.Count == 1; });
        lock (events) Assert.Equal((new SyncedRoom("book club", key.ToString()), true), events[0]);
        Assert.Contains(d.Second.SyncedRooms, r => r.Key == key.ToString());

        await TestUtil.WaitFor(() => d.Second.Rooms.Any(r => r.Name == "book club"));
        await Task.Delay(300);
        lock (events) Assert.Single(events);   // joining because of the event did not announce the room again
        await secret.LeaveAsync();
        await TestUtil.WaitFor(() => { lock (events) return events.Count == 2; });
        lock (events) Assert.False(events[1].Joined);
        Assert.DoesNotContain(d.Second.SyncedRooms, r => r.Key == key.ToString());
    }

    [Fact]
    public async Task A_device_that_was_off_gets_what_changed_when_it_comes_back_and_the_newer_change_wins()
    {
        var hub = new InMemoryDiscoveryHub();
        using var firstId = PeerIdentity.Generate();
        using var secondId = firstId.CreateDevice();
        var path = Path.Combine(Path.GetTempPath(), "yaparr-acct-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var second = TestUtil.NewNode(hub, "alice", identity: secondId, accountStatePath: path))
            {
                second.Rename("old name");   // stamped before the first device ever changes anything
            }

            using var first = TestUtil.NewNode(hub, "alice", identity: firstId);
            first.Rename("newer name");

            using var back = TestUtil.NewNode(hub, "alice", identity: secondId, accountStatePath: path);
            Assert.Equal("alice", back.DisplayName);   // the name at start comes from the app; the file only holds the stamps
            var r1 = await first.JoinRoomAsync("lobby");
            var r2 = await back.JoinRoomAsync("lobby");
            await TestUtil.WaitFor(() => r1.Members.Count == 1 && r2.Members.Count == 1);
            await TestUtil.WaitFor(() => back.DisplayName == "newer name");
            Assert.Equal("newer name", first.DisplayName);
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    [Fact]
    public async Task Someone_else_cannot_change_your_settings_by_sending_the_same_message()
    {
        using var d = await Devices.StartAsync();
        var toFirst = d.Other.Network.Get(d.First.Id)!;
        var forged = "{\"t\":\"acct\",\"entries\":[{\"key\":\"name\",\"value\":\"hacked\",\"ts\":99999999999999,\"deleted\":false}]}";
        toFirst.SendReliable(ChannelIds.Chat, Encoding.UTF8.GetBytes(forged));

        await Task.Delay(1000);
        Assert.Equal("alice", d.First.DisplayName);
        Assert.Equal("alice", d.Second.DisplayName);
    }
}

public class DeviceLinkTests
{
    private static readonly AccountEntry[] Entries =
    {
        new("room:abc", "{\"n\":\"book club\",\"k\":\"secret\"}", 5, false),
        new("block:" + new string('a', 64), "1", 7, false),
    };

    [Fact]
    public void A_code_opens_with_its_passphrase_into_a_new_device_of_the_same_account()
    {
        using var first = PeerIdentity.Generate();
        var passphrase = DeviceLink.GeneratePassphrase();
        var code = DeviceLink.Create(first, "alice", Entries, passphrase);

        var linked = DeviceLink.Open(code, passphrase.ToLowerInvariant().Replace("-", " "));
        using var identity = linked.Identity;
        Assert.Equal(first.AccountId, identity.AccountId);
        Assert.NotEqual(first.Id, identity.Id);
        Assert.True(identity.CanLinkDevices);
        Assert.Equal("alice", linked.DisplayName);
        Assert.Equal(Entries, linked.Entries);
    }

    [Fact]
    public void Each_code_makes_a_different_device()
    {
        using var first = PeerIdentity.Generate();
        var pass = DeviceLink.GeneratePassphrase();
        using var one = DeviceLink.Open(DeviceLink.Create(first, "a", Array.Empty<AccountEntry>(), pass), pass).Identity;
        using var two = DeviceLink.Open(DeviceLink.Create(first, "a", Array.Empty<AccountEntry>(), pass), pass).Identity;
        Assert.NotEqual(one.Id, two.Id);
    }

    [Fact]
    public void A_wrong_passphrase_a_damaged_code_and_a_non_code_are_all_refused()
    {
        using var first = PeerIdentity.Generate();
        var code = DeviceLink.Create(first, "alice", Entries, "AAAA-BBBB-CCCC-DDDD-EEEE");

        Assert.Throws<CryptographicException>(() => DeviceLink.Open(code, "AAAA-BBBB-CCCC-DDDD-EEEF"));
        var damaged = code[..^3] + (code[^3] == 'A' ? "B" : "A") + code[^2..];
        Assert.ThrowsAny<Exception>(() => DeviceLink.Open(damaged, "AAAA-BBBB-CCCC-DDDD-EEEE"));
        Assert.Throws<FormatException>(() => DeviceLink.Open("hello", "AAAA-BBBB-CCCC-DDDD-EEEE"));
        Assert.Throws<FormatException>(() => DeviceLink.Open("YLNK1.AAAA", "AAAA-BBBB-CCCC-DDDD-EEEE"));
    }

    [Fact]
    public void A_code_stops_working_after_a_day()
    {
        using var first = PeerIdentity.Generate();
        var old = DeviceLink.CreateAt(first, "alice", Entries, "AAAA-BBBB-CCCC-DDDD-EEEE", DateTimeOffset.UtcNow.AddHours(-25));
        Assert.Throws<InvalidOperationException>(() => DeviceLink.Open(old, "AAAA-BBBB-CCCC-DDDD-EEEE"));
        var fresh = DeviceLink.CreateAt(first, "alice", Entries, "AAAA-BBBB-CCCC-DDDD-EEEE", DateTimeOffset.UtcNow.AddHours(-23));
        DeviceLink.Open(fresh, "AAAA-BBBB-CCCC-DDDD-EEEE").Identity.Dispose();
    }

    [Fact]
    public async Task A_device_linked_from_a_code_arrives_with_the_rooms_and_blocks_and_meets_the_first_device()
    {
        var hub = new InMemoryDiscoveryHub();
        using var firstId = PeerIdentity.Generate();
        using var first = TestUtil.NewNode(hub, "alice", identity: firstId);
        var nuisance = PeerIdentity.Generate().Id;
        first.Block(nuisance);
        var key = RoomKey.Generate();
        var room = await first.JoinPrivateRoomAsync(key, "book club");

        var pass = DeviceLink.GeneratePassphrase();
        var linked = DeviceLink.Open(DeviceLink.Create(firstId, first.DisplayName, first.AccountEntries, pass), pass);
        using var secondId = linked.Identity;
        using var second = TestUtil.NewNode(hub, linked.DisplayName, identity: secondId, accountEntries: linked.Entries,
            blocked: first.BlockedPeers);

        Assert.Equal(first.AccountId, second.AccountId);
        Assert.Equal("alice", second.DisplayName);
        Assert.Contains(second.SyncedRooms, r => r.Key == key.ToString() && r.Name == "book club");
        Assert.True(second.IsBlocked(nuisance));

        var room2 = await second.JoinPrivateRoomAsync(key, "book club");
        await TestUtil.WaitFor(() => room.Members.Count == 1 && room2.Members.Count == 1);
        Assert.Contains(room.Members, m => m.Id == second.Id && m.Account == first.AccountId);

        // The device saw the room and the block in the account settings, so joining it did not announce anything new.
        await Task.Delay(300);
        Assert.Single(first.AccountEntries.Where(e => e.Key.StartsWith("room:")));
    }
}
