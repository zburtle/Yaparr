using System.Net;
using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class ViewingTests
{
    private const string Url = "https://cloud.yaparr.com/v1/push/wake/AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCde";

    private static ChatNode Node(InMemoryDiscoveryHub hub, string name, PeerIdentity identity, IReadOnlyList<AccountEntry>? entries = null, Func<string, Task<WakeResult>>? wake = null) => new(new ChatNodeOptions
    {
        DisplayName = name,
        Identity = identity,
        AccountEntries = entries ?? Array.Empty<AccountEntry>(),
        DiscoveryFactory = mux => hub.CreateClient(new IPEndPoint(IPAddress.Loopback, mux.LocalPort)),
        DiscoveryInterval = TimeSpan.FromMilliseconds(300),
        Network = TestUtil.FastNetwork,
        WakePeer = wake,
    });

    // The computer and the phone of one person, online together.
    private static (ChatNode Computer, ChatNode Phone) Pair(InMemoryDiscoveryHub hub)
    {
        var firstId = PeerIdentity.Generate();
        var computer = Node(hub, "alice", firstId);
        var pass = DeviceLink.GeneratePassphrase();
        var linked = DeviceLink.Open(DeviceLink.Create(firstId, computer.DisplayName, computer.AccountEntries, pass), pass);
        var phone = Node(hub, "alice", linked.Identity, linked.Entries);
        return (computer, phone);
    }

    [Fact]
    public async Task A_phone_knows_which_chat_the_computer_of_the_same_person_is_showing_and_for_how_long()
    {
        var hub = new InMemoryDiscoveryHub();
        var (computer, phone) = Pair(hub);
        using var _1 = computer;
        using var _2 = phone;
        var key = RoomKey.Generate();
        var room = await computer.JoinRoomAsync(key, "Home");
        await phone.JoinRoomAsync(key, "Home");
        await TestUtil.WaitFor(() => phone.MyDevices.Count > 1 && computer.MyDevices.Count > 1, 30_000);

        Assert.False(phone.SiblingIsViewing(room.RoomId, null, null));
        Assert.False(phone.SiblingIsActive());
        computer.SetViewing(new ViewTarget(room.RoomId, null, null));
        await TestUtil.WaitFor(() => phone.SiblingIsViewing(room.RoomId, null, null), 20_000);
        Assert.True(phone.SiblingIsActive());   // in use, whichever chat it shows (a chat the phone is not showing waits a little before the computer says so)
        Assert.True(phone.SiblingIsViewing(room.RoomId, RoomChannels.DefaultText, null));
        Assert.False(phone.SiblingIsViewing(room.RoomId, "some-other-channel", null));

        computer.SetViewing(null);
        await TestUtil.WaitFor(() => !phone.SiblingIsViewing(room.RoomId, null, null), 20_000);
        Assert.False(phone.SiblingIsActive());
    }

    [Fact]
    public async Task What_someone_elses_device_shows_is_not_taken_for_yours()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = Node(hub, "alice", PeerIdentity.Generate());
        using var b = Node(hub, "bob", PeerIdentity.Generate());
        var key = RoomKey.Generate();
        var ra = await a.JoinRoomAsync(key, "Home");
        await b.JoinRoomAsync(key, "Home");
        await TestUtil.WaitFor(() => ra.Members.Count == 1, 20_000);

        a.SetViewing(new ViewTarget(ra.RoomId, null, null));
        await Task.Delay(500);
        Assert.False(b.SiblingIsViewing(ra.RoomId, null, null));
    }

    [Fact]
    public void A_chat_shown_on_another_device_of_the_person_gets_no_notification_on_the_phone()
    {
        var shown = new NotificationPolicy.Message(IsDirect: false, Mine: false, MentionsMe: true, ReplyToMe: false, RoomNotifiesAll: false, TimeSpan.FromSeconds(5), ShownElsewhere: true);
        Assert.False(NotificationPolicy.ShouldNotify(shown, appInForeground: false, notificationsOn: true));
        Assert.True(NotificationPolicy.ShouldNotify(shown with { ShownElsewhere = false }, appInForeground: false, notificationsOn: true));
        var direct = new NotificationPolicy.Message(IsDirect: true, Mine: false, MentionsMe: false, ReplyToMe: false, RoomNotifiesAll: false, TimeSpan.FromSeconds(5), ShownElsewhere: true);
        Assert.False(NotificationPolicy.ShouldNotify(direct, appInForeground: false, notificationsOn: true));
    }

    // The phone is asleep (not connected to the sender) while the same person's computer is connected to the sender.
    private static async Task<(ChatNode Sender, ChatRoom Room, List<string> Asked, ChatNode Computer, ChatRoom ComputerRoom)> Setup()
    {
        var hub = new InMemoryDiscoveryHub();
        var asked = new List<string>();
        var senderId = PeerIdentity.Generate();
        var sender = Node(hub, "sender", senderId, wake: u => { lock (asked) asked.Add(u); return Task.FromResult(WakeResult.Sent); });
        var (computer, phone) = Pair(hub);
        phone.WakeAddressOfMine = Url;
        var key = RoomKey.Generate();
        var room = await sender.JoinRoomAsync(key, "Home");
        var computerRoom = await computer.JoinRoomAsync(key, "Home");
        await phone.JoinRoomAsync(key, "Home");
        await TestUtil.WaitFor(() => room.Members.Count == 2, 30_000);
        await TestUtil.WaitFor(() => sender.KnownWakeAddresses == 1, 30_000);
        // The phone goes to sleep: it is no longer connected, and the computer is.
        phone.Dispose();
        await TestUtil.WaitFor(() => room.Members.Count == 1, 30_000);
        return (sender, room, asked, computer, computerRoom);
    }

    [Fact]
    public async Task A_message_that_names_the_person_wakes_the_phone_even_when_their_computer_is_connected_and_other_messages_do_not()
    {
        var (sender, room, asked, computer, _) = await Setup();
        using var _1 = sender;
        using var _2 = computer;

        await room.SendAsync("anyone around?");
        await Task.Delay(500);
        lock (asked) Assert.Empty(asked);   // the computer is connected: nobody needs waking

        await room.SendAsync(MessageEnvelope.Wrap("@alice look", null, new[] { new Mention(computer.AccountId, "alice") }), RoomChannels.DefaultText);
        await TestUtil.WaitFor(() => { lock (asked) return asked.Count == 1; }, 10_000);
        Assert.Equal(Url, asked[0]);
    }

    [Fact]
    public async Task An_answer_to_a_message_of_the_person_wakes_the_phone_too()
    {
        var (sender, room, asked, computer, computerRoom) = await Setup();
        using var _1 = sender;
        using var _2 = computer;
        var heard = new List<ChatMessage>();
        room.MessageReceived += m => { lock (heard) heard.Add(m); };

        // The person's computer wrote a message in the room (the phone was asleep), and the sender answers it.
        var mine = await computerRoom.SendAsync("hello from my computer");
        await TestUtil.WaitFor(() => { lock (heard) return heard.Any(m => m.Id == mine); }, 10_000);
        ChatMessage original;
        lock (heard) original = heard.First(m => m.Id == mine);
        await room.SendAsync(MessageEnvelope.Wrap("answering", original.Key, null), RoomChannels.DefaultText);
        await TestUtil.WaitFor(() => { lock (asked) return asked.Count == 1; }, 10_000);
        Assert.Equal(Url, asked[0]);
    }
}
