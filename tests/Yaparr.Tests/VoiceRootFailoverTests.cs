using System.Diagnostics;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Voice;

namespace Yaparr.Tests;

// Timings as the app has them, not sped up: how long a big call is without audio when the root of its tree disappears.
[Collection("real-time audio")]
public class VoiceRootFailoverTests
{
    [Fact]
    public async Task When_the_root_of_a_big_call_goes_silent_without_a_goodbye_everyone_moves_to_a_new_tree_in_seconds()
    {
        const int n = 10;
        var hub = new InMemoryDiscoveryHub();
        var nodes = new List<ChatNode>();
        var rooms = new List<ChatRoom>();
        var sessions = new List<VoiceSession>();
        try
        {
            for (int i = 0; i < n; i++) nodes.Add(TestUtil.NewNode(hub, "v" + i));
            foreach (var node in nodes) rooms.Add(await node.JoinRoomAsync("failover"));
            await TestUtil.WaitFor(() => rooms.All(r => r.Members.Count >= 1), 30_000);
            foreach (var room in rooms) { var s = room.JoinVoice(); s.Activation = VoiceActivation.Open; sessions.Add(s); }
            await TestUtil.WaitFor(() => rooms.All(r => r.VoiceParticipants.Count == n), 60_000);
            await TestUtil.WaitFor(() => rooms.All(r => r.Media.Values.Any(g => g.Kind == "voice" && g.Attached && g.Joined)), 120_000);

            int rootIndex = rooms.FindIndex(r => r.Media.Values.Any(g => g.Kind == "voice" && g.IsRoot));
            string firstGroup = rooms[rootIndex].Media.Values.Single(g => g.Kind == "voice").Id;

            // No goodbye: the root's machine just stops sending (a crash, a cable pulled). Its connections look alive for another half minute.
            var muxField = typeof(ChatNode).GetField("_mux", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            ((Yaparr.Transport.UdpMux)muxField.GetValue(nodes[rootIndex])!).OutboundFilter = (_, _) => false;
            var watch = Stopwatch.StartNew();

            var alive = Enumerable.Range(0, n).Where(i => i != rootIndex).ToList();
            await TestUtil.WaitFor(() => alive.All(i => rooms[i].Media.Values.Any(g => g.Kind == "voice" && g.Id != firstGroup && g.Attached && g.Joined)), 60_000);

            // It used to take the whole half minute the old tree's announcements stay valid.
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed.TotalSeconds:0.0} s");
            Assert.Single(alive.Select(i => rooms[i].Media.Values.Single(g => g.Kind == "voice" && g.Id != firstGroup && g.Joined).Id).Distinct());
        }
        finally
        {
            foreach (var s in sessions) { try { s.Dispose(); } catch (ObjectDisposedException) { } }
            foreach (var node in nodes) { try { node.Dispose(); } catch (ObjectDisposedException) { } }
        }
    }
}
