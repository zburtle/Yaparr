using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

// A screen can be shared from a linked device that is not in the call, while another device of the same account is. With none of your
// devices in the call there is nothing to share into.
public class ShareFromLinkedDeviceTests
{
    private sealed class Setup : IDisposable
    {
        public readonly InMemoryDiscoveryHub Hub = new();
        public readonly PeerIdentity DesktopId = PeerIdentity.Generate();
        public PeerIdentity LaptopId = null!;
        public ChatNode Desktop = null!, Laptop = null!, Friend = null!;
        public ChatRoom OnDesktop = null!, OnLaptop = null!, OnFriend = null!;

        public static async Task<Setup> StartAsync()
        {
            var s = new Setup();
            s.LaptopId = s.DesktopId.CreateDevice();
            s.Desktop = TestUtil.NewNode(s.Hub, "zach", identity: s.DesktopId);
            s.Laptop = TestUtil.NewNode(s.Hub, "zach", identity: s.LaptopId);
            s.Friend = TestUtil.NewNode(s.Hub, "bob");
            s.OnDesktop = await s.Desktop.JoinRoomAsync("lobby");
            s.OnLaptop = await s.Laptop.JoinRoomAsync("lobby");
            s.OnFriend = await s.Friend.JoinRoomAsync("lobby");
            await TestUtil.WaitFor(() => s.OnDesktop.Members.Count == 2 && s.OnLaptop.Members.Count == 2 && s.OnFriend.Members.Count == 2);
            return s;
        }

        public void Dispose()
        {
            Desktop.Dispose(); Laptop.Dispose(); Friend.Dispose();
            DesktopId.Dispose(); LaptopId.Dispose();
        }
    }

    [Fact]
    public async Task With_none_of_your_devices_in_voice_there_is_nothing_to_share_into()
    {
        using var s = await Setup.StartAsync();
        Assert.Null(s.OnDesktop.ScreenShareChannel);
        Assert.False(s.OnLaptop.CanShareScreen);

        // Someone else being in the call does not count.
        using var friendsCall = s.OnFriend.JoinVoice();
        await TestUtil.WaitFor(() => s.OnLaptop.VoiceParticipants.Any(p => p.Peer.DisplayName == "bob"));
        Assert.False(s.OnLaptop.CanShareScreen);
        Assert.False(s.OnDesktop.CanShareScreen);
    }

    [Fact]
    public async Task A_device_that_is_not_in_the_call_can_share_into_the_one_another_of_your_devices_is_in()
    {
        using var s = await Setup.StartAsync();
        var lounge = s.OnDesktop.Channels.First(c => c.Kind == ChannelKind.Voice);
        using var call = s.OnDesktop.JoinVoice(lounge.Id);

        await TestUtil.WaitFor(() => s.OnLaptop.CanShareScreen);
        Assert.Equal(lounge.Id, s.OnLaptop.ScreenShareChannel);
        // The device that is in the call shares into its own channel.
        Assert.Equal(lounge.Id, s.OnDesktop.ScreenShareChannel);
    }

    [Fact]
    public async Task The_other_device_leaving_the_call_ends_the_possibility_to_share()
    {
        using var s = await Setup.StartAsync();
        var call = s.OnDesktop.JoinVoice();
        await TestUtil.WaitFor(() => s.OnLaptop.CanShareScreen);

        call.Dispose();
        await TestUtil.WaitFor(() => !s.OnLaptop.CanShareScreen);
        Assert.Null(s.OnLaptop.ScreenShareChannel);
    }

    [Fact]
    public async Task A_screen_shared_from_the_laptop_goes_to_the_call_of_the_desktop_and_the_room_sees_it_there()
    {
        using var s = await Setup.StartAsync();
        var voice = s.OnDesktop.Channels.Where(c => c.Kind == ChannelKind.Voice).ToList();
        var lounge = voice[0];
        using var call = s.OnDesktop.JoinVoice(lounge.Id);
        await TestUtil.WaitFor(() => s.OnLaptop.CanShareScreen);

        using var share = s.Laptop.StartMedia(s.OnLaptop, "screen", "zach's screen", channel: s.OnLaptop.ScreenShareChannel);
        await TestUtil.WaitFor(() => s.OnFriend.MediaGroups.Any(g => g.Kind == "screen"));
        var seen = s.OnFriend.MediaGroups.First(g => g.Kind == "screen");
        Assert.Equal(lounge.Id, seen.Channel ?? RoomChannels.DefaultVoice);
        Assert.Equal(s.Laptop.Id, seen.Root);
        // The one who shares is one of the desktop's own devices, so the desktop knows the share is its own.
        Assert.True(s.Desktop.IsMyDevice(seen.Root));
        Assert.False(s.Friend.IsMyDevice(seen.Root));
    }
}
