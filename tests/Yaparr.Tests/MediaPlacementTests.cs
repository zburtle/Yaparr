using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class MediaPlacementTests
{
    private static PeerId P(string name) => new(name.PadRight(64, '0'));

    private static MediaGroup Group(out PeerId root, out PeerId a, out PeerId b)
    {
        root = P("r"); a = P("a"); b = P("b");
        var g = new MediaGroup { Info = new MediaGroupInfo("g", "screen", "test", root, true, false), RootFanout = 1 };
        // root -> a -> b; the root has no free place left, a and b have one each.
        g.Map[a] = new MapNode(root, 2);
        g.Map[b] = new MapNode(a, 1);
        return g;
    }

    [Fact]
    public void Without_any_reach_the_shallowest_free_place_is_chosen()
    {
        var g = Group(out _, out var a, out _);
        Assert.Equal(a, ChatNode.ChoosePlace(g, P("j"), new HashSet<PeerId>()));
    }

    [Fact]
    public void A_parent_the_joiner_is_already_connected_to_is_preferred_over_a_shallower_one()
    {
        var g = Group(out _, out var a, out var b);
        Assert.Equal(b, ChatNode.ChoosePlace(g, P("j"), new HashSet<PeerId>(), new HashSet<PeerId> { b }));
        // Connected to the shallow one, or to none that has room: the usual rule.
        Assert.Equal(a, ChatNode.ChoosePlace(g, P("j"), new HashSet<PeerId>(), new HashSet<PeerId> { a }));
        Assert.Equal(a, ChatNode.ChoosePlace(g, P("j"), new HashSet<PeerId>(), new HashSet<PeerId> { P("x") }));
    }

    [Fact]
    public void A_reached_parent_that_was_ruled_out_is_not_chosen()
    {
        var g = Group(out _, out var a, out var b);
        Assert.Equal(a, ChatNode.ChoosePlace(g, P("j"), new HashSet<PeerId> { b }, new HashSet<PeerId> { b }));
    }
}
