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
    public void A_sharer_with_fewer_places_than_it_started_with_is_not_given_more_joiners()
    {
        var root = P("r");
        var g = new MediaGroup { Info = new MediaGroupInfo("g", "screen", "test", root, true, false), RootFanout = 3 };
        g.Map[P("a")] = new MapNode(root, 0);
        // One place (its upload only carries one copy) and it is taken: the next joiner is not placed under it.
        Assert.Null(ChatNode.ChoosePlace(g, P("j"), new HashSet<PeerId>(), rootPlaces: 1));
        // With the places it was started with there would be room.
        Assert.Equal(root, ChatNode.ChoosePlace(g, P("j"), new HashSet<PeerId>()));
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

    [Fact]
    public void What_a_member_says_about_itself_replaces_what_the_root_was_told()
    {
        var g = Group(out var root, out var a, out var b);

        ChatNode.ReconcileMap(g, b, root, 1);                       // b says it sits under the root, but the root never took it on
        Assert.False(g.Map.ContainsKey(b));

        ChatNode.ReconcileMap(g, a, null, 0);                       // a says it is not attached at all
        Assert.False(g.Map.ContainsKey(a));

        g.Children[a] = new ChildLink { Cap = 2 };
        ChatNode.ReconcileMap(g, a, root, 2);                       // a is a child of the root after all
        Assert.Equal(new MapNode(root, 2), g.Map[a]);

        ChatNode.ReconcileMap(g, b, a, 500);                        // b under a again; the place count is kept sane
        Assert.Equal(new MapNode(a, 64), g.Map[b]);

        ChatNode.ReconcileMap(g, a, a, 1);                          // nobody sits under themselves
        Assert.False(g.Map.ContainsKey(a));
        ChatNode.ReconcileMap(g, root, a, 1);                       // and the sharer is never moved
        Assert.False(g.Map.ContainsKey(root));
    }

    [Fact]
    public void A_member_the_map_puts_under_the_root_without_the_root_having_taken_it_on_is_dropped()
    {
        var g = Group(out var root, out var a, out var b);
        g.Map[P("liar")] = new MapNode(root, 3);
        g.Children[a] = new ChildLink { Cap = 2 };

        ChatNode.DropFalseRootChildren(g);

        Assert.False(g.Map.ContainsKey(P("liar")));
        Assert.True(g.Map.ContainsKey(a));
        Assert.True(g.Map.ContainsKey(b));
    }
}
