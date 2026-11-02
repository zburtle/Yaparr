using Yaparr.Chat;
using Yaparr.Discovery;

namespace Yaparr.Tests;

public class RoomPeopleTests
{
    [Fact]
    public async Task A_room_lists_the_people_seen_in_it_online_or_not_and_not_those_of_another_room()
    {
        var hub = new InMemoryDiscoveryHub();
        using var owner = TestUtil.NewNode(hub, "owner");
        using var bob = TestUtil.NewNode(hub, "bob");
        var carol = TestUtil.NewNode(hub, "carol");
        using var dave = TestUtil.NewNode(hub, "dave");

        var lobbyKey = RoomKey.Generate(owner.AccountId);
        var lobby = await owner.JoinRoomAsync(lobbyKey, "lobby");
        await bob.JoinRoomAsync(RoomKey.Parse(lobbyKey.ToString()), "lobby");
        await carol.JoinRoomAsync(RoomKey.Parse(lobbyKey.ToString()), "lobby");

        // A second room that only dave is in with the owner.
        var otherKey = RoomKey.Generate(owner.AccountId);
        var other = await owner.JoinRoomAsync(otherKey, "other");
        await dave.JoinRoomAsync(RoomKey.Parse(otherKey.ToString()), "other");

        await TestUtil.WaitFor(() => lobby.People.Count(p => p.Online) == 2 && other.People.Count(p => p.Online) == 1, 30_000);

        var lobbyPeople = lobby.People;
        Assert.Contains(lobbyPeople, p => p.Account == bob.AccountId && p.Name == "bob" && p.Online);
        Assert.Contains(lobbyPeople, p => p.Account == carol.AccountId && p.Online);
        Assert.DoesNotContain(lobbyPeople, p => p.Account == dave.AccountId);        // dave is in the other room only
        Assert.DoesNotContain(lobbyPeople, p => p.Account == owner.AccountId);       // not yourself
        Assert.Single(other.People);
        Assert.Equal(dave.AccountId, other.People[0].Account);

        // Carol goes away: she is still listed in the room, but no longer online. Bob is unaffected.
        await TestUtil.WaitFor(() => lobby.People.Any(p => p.Account == carol.AccountId && p.Online));
        carol.Dispose();
        await TestUtil.WaitFor(() => lobby.People.Any(p => p.Account == carol.AccountId && !p.Online), 30_000);
        Assert.Contains(lobby.People, p => p.Account == bob.AccountId && p.Online);
        Assert.DoesNotContain(other.People, p => p.Account == carol.AccountId);
    }
}
