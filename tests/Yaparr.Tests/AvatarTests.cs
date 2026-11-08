using SkiaSharp;
using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class AvatarTests
{
    // The smallest thing that begins like a PNG file; the node does not decode pictures.
    private static byte[] Picture(byte seed) => new byte[] { 0x89, 0x50, 0x4E, 0x47, seed, 1, 2, 3, 4, 5, 6, 7 };

    [Fact]
    public async Task A_picture_reaches_the_people_in_the_room_and_is_taken_away_again()
    {
        var hub = new InMemoryDiscoveryHub();
        using var alice = TestUtil.NewNode(hub, "alice");
        using var bob = TestUtil.NewNode(hub, "bob");
        var ra = await alice.JoinRoomAsync("lobby");
        var rb = await bob.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => ra.Members.Count == 1 && rb.Members.Count == 1);
        Assert.Null(bob.AvatarOf(alice.AccountId));

        var picture = Picture(1);
        alice.SetAvatar(picture);
        Assert.Equal(picture, alice.MyAvatar);
        await TestUtil.WaitFor(() => bob.AvatarOf(alice.AccountId) != null);
        Assert.Equal(picture, bob.AvatarOf(alice.AccountId));

        alice.SetAvatar(null);
        await TestUtil.WaitFor(() => bob.AvatarOf(alice.AccountId) == null);
    }

    [Fact]
    public async Task Someone_who_connects_later_is_told_of_the_picture()
    {
        var hub = new InMemoryDiscoveryHub();
        using var alice = TestUtil.NewNode(hub, "alice");
        alice.SetAvatar(Picture(2));
        using var bob = TestUtil.NewNode(hub, "bob");
        var ra = await alice.JoinRoomAsync("lobby");
        var rb = await bob.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => bob.AvatarOf(alice.AccountId) != null);
        Assert.Equal(Picture(2), bob.AvatarOf(alice.AccountId));
    }

    [Fact]
    public async Task A_picture_is_passed_on_to_a_member_who_is_only_connected_through_someone_else()
    {
        var hub = new InMemoryDiscoveryHub();
        using var alice = TestUtil.NewNode(hub, "alice");
        using var bob = TestUtil.NewNode(hub, "bob");
        using var carol = TestUtil.NewNode(hub, "carol");
        var ra = await alice.JoinRoomAsync("lobby");
        var rb = await bob.JoinRoomAsync("lobby");
        var rc = await carol.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => ra.Members.Count >= 1 && rb.Members.Count >= 1 && rc.Members.Count >= 1);
        alice.SetAvatar(Picture(3));
        await TestUtil.WaitFor(() => bob.AvatarOf(alice.AccountId) != null && carol.AvatarOf(alice.AccountId) != null);
    }

    [Fact]
    public async Task A_picture_set_on_one_device_is_the_accounts_on_the_other()
    {
        var hub = new InMemoryDiscoveryHub();
        using var firstId = PeerIdentity.Generate();
        using var secondId = firstId.CreateDevice();
        using var desktop = TestUtil.NewNode(hub, "zach", identity: firstId);
        using var phone = TestUtil.NewNode(hub, "zach", identity: secondId);
        using var friend = TestUtil.NewNode(hub, "bob");
        var rd = await desktop.JoinRoomAsync("lobby");
        var rp = await phone.JoinRoomAsync("lobby");
        var rf = await friend.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => rd.Members.Count == 2 && rp.Members.Count == 2 && rf.Members.Count == 2);

        phone.SetAvatar(Picture(4));
        await TestUtil.WaitFor(() => desktop.MyAvatar != null && friend.AvatarOf(desktop.AccountId) != null);
        Assert.Equal(Picture(4), desktop.MyAvatar);
        Assert.Equal(Picture(4), friend.AvatarOf(phone.AccountId));
    }

    [Fact]
    public async Task A_forged_claim_or_a_picture_that_does_not_match_is_ignored()
    {
        var hub = new InMemoryDiscoveryHub();
        using var alice = TestUtil.NewNode(hub, "alice");
        using var bob = TestUtil.NewNode(hub, "bob");
        Assert.Throws<ArgumentException>(() => alice.SetAvatar(new byte[] { 1, 2, 3, 4, 5 }));
        Assert.Throws<ArgumentException>(() => alice.SetAvatar(new byte[ChatNode.MaxAvatarBytes + 1]));
        Assert.False(ChatNode.LooksLikeAvatar(Array.Empty<byte>()));
        Assert.True(ChatNode.LooksLikeAvatar(Picture(5)));
        Assert.Null(bob.AvatarOf(alice.AccountId));
    }

    [Fact]
    public async Task Pictures_are_kept_between_runs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-avatars-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hub = new InMemoryDiscoveryHub();
            using var id = PeerIdentity.Generate();
            PeerId account;
            using (var first = TestUtil.NewNode(hub, "alice", identity: id, avatarDirectory: dir))
            {
                account = first.AccountId;
                first.SetAvatar(Picture(6));
            }
            using var again = TestUtil.NewNode(new InMemoryDiscoveryHub(), "alice", identity: id, avatarDirectory: dir);
            Assert.Equal(Picture(6), again.AvatarOf(account));
            await Task.CompletedTask;
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Any_picture_is_made_into_a_small_square_that_the_network_accepts()
    {
        using var wide = new SKBitmap(900, 400);
        using (var canvas = new SKCanvas(wide))
        {
            canvas.Clear(SKColors.CornflowerBlue);
            using var paint = new SKPaint { Color = SKColors.OrangeRed };
            canvas.DrawCircle(450, 200, 150, paint);
        }
        using var png = SKImage.FromBitmap(wide).Encode(SKEncodedImageFormat.Png, 100);
        var avatar = ImageTools.Avatar(png.ToArray());
        Assert.True(ChatNode.LooksLikeAvatar(avatar));
        Assert.InRange(avatar.Length, 100, ChatNode.MaxAvatarBytes);
        using var decoded = SKBitmap.Decode(avatar);
        Assert.Equal(decoded.Width, decoded.Height);
        Assert.InRange(decoded.Width, 64, 128);
        Assert.Throws<ArgumentException>(() => ImageTools.Avatar(new byte[] { 1, 2, 3, 4 }));
    }
}
