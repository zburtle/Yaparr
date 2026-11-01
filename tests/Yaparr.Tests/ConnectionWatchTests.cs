using Yaparr.Chat;

namespace Yaparr.Tests;

public class ConnectionWatchTests
{
    private const long Second = 1000;

    private static (ConnectionWatch Watch, List<string> Events) Make()
    {
        var watch = new ConnectionWatch();
        var events = new List<string>();
        watch.Lost += () => events.Add("lost");
        watch.Restored += () => events.Add("restored");
        return (watch, events);
    }

    // Feeds the same count once a second.
    private static void Hold(ConnectionWatch watch, ref long now, int peers, int seconds)
    {
        for (int i = 0; i < seconds; i++) { watch.Update(peers, now); now += Second; }
    }

    [Fact]
    public void Losing_everyone_you_were_connected_to_is_announced_after_a_few_seconds_and_so_is_the_return()
    {
        var (watch, events) = Make();
        long now = 1_000_000;
        Hold(watch, ref now, 3, 10);
        Hold(watch, ref now, 0, 2);
        Assert.Empty(events);              // not yet: it may only be a hiccup
        Hold(watch, ref now, 0, 4);
        Assert.Equal(new[] { "lost" }, events);
        Assert.True(watch.Reconnecting);
        Assert.True(watch.LostFor(now) > TimeSpan.Zero);

        Hold(watch, ref now, 0, 30);       // staying lost says nothing more
        Assert.Equal(new[] { "lost" }, events);

        Hold(watch, ref now, 1, 1);
        Assert.Equal(new[] { "lost", "restored" }, events);
        Assert.False(watch.Reconnecting);
    }

    [Fact]
    public void One_person_going_offline_is_not_a_lost_connection()
    {
        var (watch, events) = Make();
        long now = 0;
        Hold(watch, ref now, 1, 10);
        Hold(watch, ref now, 0, 60);
        Assert.Empty(events);
    }

    [Fact]
    public void One_person_gone_just_after_the_network_changed_is_a_lost_connection()
    {
        var (watch, events) = Make();
        long now = 0;
        Hold(watch, ref now, 1, 10);
        watch.NetworkChanged(now);
        Hold(watch, ref now, 0, 6);
        Assert.Equal(new[] { "lost" }, events);
    }

    [Fact]
    public void A_flicker_shorter_than_the_wait_and_never_having_been_connected_say_nothing()
    {
        var (watch, events) = Make();
        long now = 0;
        Hold(watch, ref now, 0, 120);      // nobody has ever been there
        Hold(watch, ref now, 4, 10);
        Hold(watch, ref now, 0, 3);        // a moment of nothing
        Hold(watch, ref now, 4, 10);
        Assert.Empty(events);
    }

    [Fact]
    public void A_second_loss_is_announced_again()
    {
        var (watch, events) = Make();
        long now = 0;
        Hold(watch, ref now, 3, 5); Hold(watch, ref now, 0, 6); Hold(watch, ref now, 3, 5);
        Hold(watch, ref now, 0, 6);
        Assert.Equal(new[] { "lost", "restored", "lost" }, events);
    }

    [Fact]
    public void In_a_call_losing_the_only_other_person_is_a_lost_connection_but_leaving_the_call_is_not()
    {
        var watch = new ConnectionWatch(minPeers: 1);
        var events = new List<string>();
        watch.Lost += () => events.Add("lost");
        watch.Restored += () => events.Add("restored");
        long now = 0;

        // One other person in the call, reachable, and then not, while still in the call.
        Hold(watch, ref now, 1, 10);
        Hold(watch, ref now, 0, 6);
        Assert.Equal(new[] { "lost" }, events);

        // They leave the call: nothing to reconnect to, and no sound for it.
        watch.Reset();
        Assert.False(watch.Reconnecting);
        Assert.Equal(new[] { "lost" }, events);
        Hold(watch, ref now, 0, 30);
        Assert.Equal(new[] { "lost" }, events);
    }

    [Fact]
    public void Joining_a_call_where_nobody_can_be_reached_yet_is_not_a_lost_connection()
    {
        var watch = new ConnectionWatch(minPeers: 1);
        var events = new List<string>();
        watch.Lost += () => events.Add("lost");
        long now = 0;
        Hold(watch, ref now, 0, 60);
        Assert.Empty(events);
    }
}
