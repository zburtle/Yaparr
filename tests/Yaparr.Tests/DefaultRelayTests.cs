using Yaparr.Chat;
using Yaparr.Transport;

namespace Yaparr.Tests;

public class DefaultRelayTests
{
    private static readonly TurnOptions BuiltIn = new() { Server = "relay.example.com:3478", Username = "u", Credential = "p", Enabled = true };
    private static readonly TurnOptions Own = new() { Server = "mine.example.com:3478", Username = "a", Credential = "b", Enabled = true };

    private static ChatNode Node(TurnOptions? own, bool useDefault) => new(new ChatNodeOptions
    {
        DefaultTurn = BuiltIn,
        Traversal = new TraversalOptions { Turn = own, UseDefaultRelay = useDefault, Enabled = false },
    });

    [Fact]
    public void The_built_in_relay_is_used_when_the_user_has_none_of_their_own()
    {
        using var node = Node(null, true);
        Assert.Equal("relay.example.com:3478", node.EffectiveTurn()!.Server);
    }

    [Fact]
    public void A_server_the_user_set_up_wins_over_the_built_in_relay()
    {
        using var node = Node(Own, true);
        Assert.Equal("mine.example.com:3478", node.EffectiveTurn()!.Server);
    }

    [Fact]
    public void A_disabled_own_server_falls_back_but_switching_the_built_in_relay_off_means_no_relay()
    {
        var off = new TurnOptions { Server = Own.Server, Enabled = false };
        using (var node = Node(off, true)) Assert.Equal("relay.example.com:3478", node.EffectiveTurn()!.Server);
        using (var node = Node(off, false)) Assert.Null(node.EffectiveTurn());
    }

    [Fact]
    public void The_built_in_relay_choice_is_saved_next_to_the_other_network_settings()
    {
        var path = Path.Combine(Path.GetTempPath(), "yaparr-net-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.True(TraversalOptions.Load(path).UseDefaultRelay);
            TraversalOptions.SaveTurn(path, Own);
            TraversalOptions.SaveDefaultRelay(path, false);
            var loaded = TraversalOptions.Load(path);
            Assert.False(loaded.UseDefaultRelay);
            Assert.Equal(Own.Server, loaded.Turn!.Server);
        }
        finally { File.Delete(path); }
    }
}
