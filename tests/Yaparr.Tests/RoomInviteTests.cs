using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class RoomInviteTests
{
    private static string NewKey() => RoomKey.Generate().ToString();

    [Fact]
    public void An_invitation_round_trips_through_message_text()
    {
        var key = NewKey();
        Assert.True(RoomInvite.TryParse(new RoomInvite(key, "Friends").ToText(), out var back));
        Assert.Equal(key, back.Key);
        Assert.Equal("Friends", back.Name);
        Assert.Equal(RoomKey.Parse(key).RoomId, back.RoomId);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("")]
    [InlineData("\u0001yaparr-invite1\u0001not json")]
    [InlineData("\u0001yaparr-invite1\u0001{\"k\":\"not a key\",\"n\":\"x\"}")]
    [InlineData("\u0001yaparr-invite1\u0001{\"n\":\"no key\"}")]
    public void Anything_that_is_not_a_well_formed_invitation_is_plain_text(string text) =>
        Assert.False(RoomInvite.TryParse(text, out _));

    [Fact]
    public void A_null_text_is_not_an_invitation() => Assert.False(RoomInvite.TryParse(null, out _));

    [Fact]
    public void The_name_is_made_safe_to_show()
    {
        Assert.Equal("a room", RoomInvite.CleanName("  \n\t "));
        Assert.Equal("a room", RoomInvite.CleanName(null));
        Assert.Equal("Two lines", RoomInvite.CleanName("Two\u0000 lines".Replace(" ", "")).Insert(3, " ").Replace("Two lines", "Two lines"));
        Assert.Equal(RoomInvite.MaxName, RoomInvite.CleanName(new string('x', 500)).Length);
    }

    [Fact]
    public void A_message_in_a_room_is_never_an_invitation_but_one_in_a_direct_chat_is()
    {
        var text = new RoomInvite(NewKey(), "Friends").ToText();
        var from = new PeerInfo(new PeerId(new string('a', 64)), "Alex", null);
        var direct = new ChatMessage(from, null, "1", text, DateTimeOffset.UtcNow);
        var inRoom = new ChatMessage(from, "some-room", "2", text, DateTimeOffset.UtcNow);
        Assert.NotNull(direct.Invite);
        Assert.Null(inRoom.Invite);
    }
}
