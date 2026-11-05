using Yaparr.Chat;
using Yaparr.Desktop;

namespace Yaparr.Tests;

public class DeepLinkTests
{
    private static readonly string Peer = new('a', 64);

    [Fact]
    public void An_app_link_and_a_shared_link_for_a_room_mean_the_same()
    {
        var invite = RoomKey.Generate().ToString();
        Assert.Equal(new DeepLink(DeepLinkKind.Join, invite), DeepLink.Parse("yaparr://join/" + invite));
        Assert.Equal(new DeepLink(DeepLinkKind.Join, invite), DeepLink.Parse("https://yaparr.com/join#" + invite));
        Assert.Equal(new DeepLink(DeepLinkKind.Join, invite), DeepLink.Parse("  yaparr://join/" + invite + "/ \n"));
    }

    [Fact]
    public void A_link_for_a_person_is_lowercased_and_checked()
    {
        Assert.Equal(new DeepLink(DeepLinkKind.Chat, Peer), DeepLink.Parse("yaparr://chat/" + Peer.ToUpperInvariant()));
        Assert.Equal(new DeepLink(DeepLinkKind.Chat, Peer), DeepLink.Parse("https://yaparr.com/chat#" + Peer));
        Assert.Null(DeepLink.Parse("yaparr://chat/" + Peer[..63]));
        Assert.Null(DeepLink.Parse("yaparr://chat/" + new string('z', 64)));
    }

    [Fact]
    public void The_room_secret_must_be_in_the_fragment_of_a_web_link()
    {
        var invite = RoomKey.Generate().ToString();
        Assert.Null(DeepLink.Parse("https://yaparr.com/join/" + invite));
        Assert.Null(DeepLink.Parse("https://yaparr.com/join?key=" + invite));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yaparr://")]
    [InlineData("yaparr://join/")]
    [InlineData("yaparr://join/not-a-key")]
    [InlineData("yaparr://wipe/everything")]
    [InlineData("https://evil.example/join#abc")]
    [InlineData("https://yaparr.com.evil.example/join#abc")]
    [InlineData("file:///etc/passwd")]
    public void Anything_else_is_ignored(string? text) => Assert.Null(DeepLink.Parse(text));

    [Fact]
    public void A_very_long_value_is_ignored()
    {
        Assert.Null(DeepLink.Parse("yaparr://join/" + new string('A', 5000)));
    }

    [Fact]
    public void The_links_to_share_put_the_secret_in_the_fragment_and_read_back()
    {
        var invite = RoomKey.Generate().ToString();
        var room = DeepLink.RoomLink(invite);
        Assert.StartsWith("https://yaparr.com/join#", room);
        Assert.Equal(invite, DeepLink.Parse(room)!.Value);
        Assert.Equal(Peer, DeepLink.Parse(DeepLink.ChatLink(Peer))!.Value);
    }

    [Fact]
    public void The_operating_system_passes_the_link_as_an_argument()
    {
        var invite = RoomKey.Generate().ToString();
        var link = DeepLink.FromArgs(new[] { "--data", "x", "yaparr://join/" + invite });
        Assert.Equal(DeepLinkKind.Join, link!.Kind);
        Assert.Equal("yaparr://join/" + invite, link.ToAppUri());
        Assert.Null(DeepLink.FromArgs(new[] { "--name", "zach" }));
    }

    [Fact]
    public async Task A_second_launch_hands_its_link_to_the_running_copy()
    {
        var name = LinkChannel.NameFor(Path.Combine(Path.GetTempPath(), "yaparr-link-test-" + Guid.NewGuid().ToString("N")));
        var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var channel = LinkChannel.Listen(name, got.SetResult);
        Assert.NotNull(channel);

        Assert.True(LinkChannel.TrySend(name, "yaparr://chat/" + Peer));
        Assert.Equal("yaparr://chat/" + Peer, await got.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void With_nothing_listening_a_launch_just_starts()
    {
        Assert.False(LinkChannel.TrySend("yaparr-link-nobody-" + Guid.NewGuid().ToString("N"), "yaparr://chat/" + Peer, timeoutMs: 100));
    }

    [Fact]
    public void Two_data_folders_do_not_share_a_channel()
    {
        Assert.NotEqual(LinkChannel.NameFor(Path.Combine(Path.GetTempPath(), "a")), LinkChannel.NameFor(Path.Combine(Path.GetTempPath(), "b")));
    }
}
