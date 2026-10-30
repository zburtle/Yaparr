using System.Text.Json;

namespace Yaparr.Tests;

// The parts of the network test tool that can be checked without any machines: scenario files, the scripts it
// generates, and how it judges what the agents report.
public class NetTestToolTests
{
    private static string ScenarioDir => Path.Combine(Runner.RepoRoot()!, "tools", "Yaparr.NetTest", "scenarios");

    private static Scenario Parse(string json)
    {
        var scenario = JsonSerializer.Deserialize<Scenario>(json, Scenario.JsonOptions)!;
        scenario.Validate();
        return scenario;
    }

    [Fact]
    public void Every_scenario_that_ships_with_the_tool_is_valid()
    {
        var files = Directory.GetFiles(ScenarioDir, "*.json");
        Assert.True(files.Length >= 8);
        foreach (var file in files)
        {
            var s = Scenario.Load(file);
            Assert.Equal(Path.GetFileNameWithoutExtension(file), s.Name);
            Assert.NotEmpty(s.Assertions);
        }
    }

    [Theory]
    [InlineData("""{"name":"x","nodes":[{"name":"a","role":"watch"},{"name":"a","role":"watch"}]}""")]
    [InlineData("""{"name":"x","nodes":[{"name":"a","role":"share"},{"name":"b","role":"share"}]}""")]
    [InlineData("""{"name":"x","nodes":[{"name":"a","role":"watch","nat":"weird"}]}""")]
    [InlineData("""{"name":"x","nodes":[{"name":"a","role":"juggle"}]}""")]
    [InlineData("""{"name":"x","nodes":[{"name":"a","role":"watch"}],"timeline":[{"atSeconds":1,"action":"explode","node":"a"}]}""")]
    [InlineData("""{"name":"x","nodes":[{"name":"a","role":"watch"}],"timeline":[{"atSeconds":1,"action":"kill","node":"nobody"}]}""")]
    [InlineData("""{"name":"x","nodes":[{"name":"a b","role":"watch"}]}""")]
    [InlineData("""{"name":"x","nodes":[{"name":"a","role":"watch","args":{"fanout":"2; rm -rf /"}}]}""")]
    [InlineData("""{"name":"x","nodes":[{"name":"a","role":"watch"}],"timeline":[{"atSeconds":1,"action":"netem","node":"a","args":"loss 5%; reboot"}]}""")]
    public void A_scenario_with_a_mistake_or_something_unsafe_is_refused(string json) =>
        Assert.Throws<InvalidDataException>(() => Parse(json));

    [Fact]
    public void Comments_and_trailing_commas_are_allowed_in_scenario_files()
    {
        var s = Parse("""
            { "name": "x", // a comment
              "nodes": [ { "name": "a", "role": "watch", }, ], }
            """);
        Assert.Single(s.Nodes);
    }

    [Fact]
    public void The_symmetric_router_gives_every_destination_its_own_port_and_the_ordinary_one_does_not()
    {
        var symmetric = Scripts.Nat("symmetric", 41000);
        var cone = Scripts.Nat("cone", 41000);
        var forwarded = Scripts.Nat("fullcone", 41000);

        Assert.Contains("MASQUERADE --random", symmetric);
        Assert.DoesNotContain("--random", cone);
        Assert.Contains("MASQUERADE", cone);
        Assert.DoesNotContain("DNAT", cone);
        Assert.Contains("--dport 41000 -j DNAT --to-destination 10.77.0.2:41000", forwarded);
        foreach (var script in new[] { symmetric, cone, forwarded })
        {
            Assert.Contains("ip netns add behind", script);
            Assert.Contains("net.ipv4.ip_forward=1", script);
        }
        Assert.Throws<ArgumentException>(() => Scripts.Nat("round", 41000));
    }

    [Fact]
    public void Blocking_a_machine_adds_and_removes_the_same_four_rules()
    {
        var on = Scripts.Block("203.0.113.9", add: true).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var off = Scripts.Block("203.0.113.9", add: false).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, on.Length);
        Assert.Equal(on.Select(l => l.Trim().Replace("iptables -I", "iptables -D")), off.Select(l => l.Trim()));
    }

    [Fact]
    public void The_first_start_script_installs_the_turn_server_only_for_the_machine_that_needs_it()
    {
        var plain = Scripts.CloudInit(turn: false, "");
        var turn = Scripts.CloudInit(turn: true, "s3cret");
        Assert.Contains("sch_netem", plain);
        Assert.DoesNotContain("coturn", plain);
        Assert.Contains("apt-get install -y -qq coturn", turn);
        Assert.Contains("user=nettest:s3cret", turn);
        Assert.Contains("defer: true", turn);
    }

    [Fact]
    public void A_machine_started_from_the_snapshot_installs_nothing()
    {
        var baked = Scripts.CloudInit(turn: false, "", shaping: true, baked: true);
        Assert.DoesNotContain("apt-get", baked);
        Assert.DoesNotContain("package_update", baked);
        Assert.Contains("modprobe sch_netem", baked);

        var turn = Scripts.CloudInit(turn: true, "s3cret", shaping: false, baked: true);
        Assert.DoesNotContain("apt-get", turn);
        Assert.Contains("systemctl enable coturn && systemctl restart coturn", turn);
        Assert.Contains("user=nettest:s3cret", turn);

        // The machine that becomes the snapshot has every tool and leaves the TURN server off.
        // A machine with nothing to run must not get an empty runcmd, which cloud-init rejects.
        var idle = Scripts.CloudInit(turn: false, "", shaping: false, baked: true);
        Assert.DoesNotContain("runcmd", idle);

        // Such a machine is ready without waiting for cloud-init, which can stay busy with the provider's own installer.
        Assert.Contains("modprobe sch_netem", Scripts.BakedReady(turn: false, shaping: true));
        Assert.Contains("systemctl restart coturn", Scripts.BakedReady(turn: true, shaping: false));
        Assert.DoesNotContain("coturn", Scripts.BakedReady(turn: false, shaping: false));

        // Scripts run on Linux, where a carriage return would become part of the last word of a line.
        Assert.DoesNotContain("\r", Scripts.BakedReady(turn: true, shaping: true));
        Assert.DoesNotContain("\r", Scripts.CloudInit(turn: true, "x", shaping: true));
        Assert.DoesNotContain("\r", Scripts.CloudInit(turn: false, "", shaping: false, baked: true));

        var bake = Scripts.BakeInit();
        Assert.Contains("iptables", bake);
        Assert.Contains("coturn", bake);
        Assert.Contains("linux-modules-extra", bake);
        Assert.Contains("disable --now coturn", bake);
    }

    // ---- judging a run -----------------------------------------------------------------------

    private static NodeResult Node(string name, string role, params string[] lines)
    {
        var node = new NodeResult(new NodeSpec { Name = name, Role = role });
        foreach (var line in lines) node.Add(line);
        return node;
    }

    private static string Hello(string id) => $$"""{"type":"hello","id":"{{id}}","pid":1}""";

    private static string Watcher(string parent, double got = 100, double span = 100, double attached = 800, double seen = 200, double first = 900, double gap = 300,
        string extra = "") =>
        $$"""{"type":"summary","role":"watch","sawGroup":true,"groupSeenAtMs":{{seen}},"attachedAtMs":{{attached}},"firstFrameAtMs":{{first}},"distinct":{{got}},"span":{{span}},"attached":true,"parent":"{{parent}}","maxFrameGapMs":{{gap}},"latencyP95Ms":120,"parentChanges":1,"retransmitted":0,"recovered":0,"overServer":false,"relayCapable":true,"nat":"Open"{{extra}}}""";

    private static List<NodeResult> Tree() => new()
    {
        Node("sharer", "share", Hello("S"), """{"type":"summary","role":"share","frames":200,"retransmitted":3}"""),
        Node("a", "watch", Hello("A"), Watcher("S")),
        Node("b", "watch", Hello("B"), Watcher("A")),
        Node("c", "watch", Hello("C"), Watcher("B")),
    };

    private static Scenario With(params AssertionSpec[] assertions) =>
        new() { Name = "t", Nodes = Tree().Select(n => n.Spec).ToList(), Assertions = assertions.ToList() };

    [Fact]
    public void The_depth_of_each_viewer_is_worked_out_from_who_is_whose_parent()
    {
        var depths = Analysis.Depths(Tree());
        Assert.Equal(1, depths["a"]);
        Assert.Equal(2, depths["b"]);
        Assert.Equal(3, depths["c"]);
    }

    [Fact]
    public void A_viewer_that_never_attached_or_whose_parent_is_unknown_has_depth_zero()
    {
        var nodes = Tree();
        nodes[2] = Node("b", "watch", Hello("B"), Watcher("somebody-else"));
        var depths = Analysis.Depths(nodes);
        Assert.Equal(0, depths["b"]);
        Assert.Equal(0, depths["c"]);
    }

    [Fact]
    public void Assertions_pass_when_the_figures_are_good_and_say_what_failed_when_they_are_not()
    {
        var nodes = Tree();
        var good = Analysis.Check(With(
            new AssertionSpec { Type = "viewersReceive", Value = 0.95 },
            new AssertionSpec { Type = "attachWithin", Value = 2 },
            new AssertionSpec { Type = "firstFrameWithin", Value = 2 },
            new AssertionSpec { Type = "treeDepthAtLeast", Value = 3 },
            new AssertionSpec { Type = "treeDepthAtMost", Value = 3 },
            new AssertionSpec { Type = "latencyP95Below", Value = 200 },
            new AssertionSpec { Type = "maxFrameGapBelow", Value = 1 },
            new AssertionSpec { Type = "retransmitsAtLeast", Value = 3 },
            new AssertionSpec { Type = "noOverServer" }), nodes);
        Assert.All(good, r => Assert.True(r.Passed, r.Message));

        nodes[3] = Node("c", "watch", Hello("C"), Watcher("B", got: 60, span: 100, attached: 9000, gap: 8000));
        var bad = Analysis.Check(With(
            new AssertionSpec { Type = "viewersReceive", Value = 0.95 },
            new AssertionSpec { Type = "attachWithin", Value = 2 },
            new AssertionSpec { Type = "maxFrameGapBelow", Value = 5 }), nodes);
        Assert.All(bad, r => Assert.False(r.Passed));
        Assert.Contains("c got 60 of 100", bad[0].Message);
        Assert.DoesNotContain("a got", bad[0].Message);
    }

    [Fact]
    public void A_killed_viewer_is_left_out_of_the_default_checks_but_an_agent_that_died_by_itself_fails_the_run()
    {
        var nodes = Tree();
        nodes[1] = Node("a", "watch", Hello("A"));          // no summary
        nodes[1].Killed = true;
        var ok = Analysis.Check(With(new AssertionSpec { Type = "viewersReceive", Value = 0.9 }), nodes);
        Assert.All(ok, r => Assert.True(r.Passed, r.Message));

        nodes[1].Killed = false;
        var failed = Analysis.Check(With(), nodes);
        Assert.Contains(failed, r => !r.Passed && r.Message.StartsWith("a:"));
    }

    [Fact]
    public void The_nat_and_server_checks_look_at_the_nodes_they_name()
    {
        var nodes = Tree();
        nodes[1] = Node("a", "watch", Hello("A"), Watcher("S").Replace("\"nat\":\"Open\"", "\"nat\":\"Symmetric\"").Replace("\"relayCapable\":true", "\"relayCapable\":false"));
        nodes[2] = Node("b", "watch", Hello("B"), Watcher("A").Replace("\"overServer\":false", "\"overServer\":true").Replace("\"attached\":true", "\"attached\":false"));
        var results = Analysis.Check(With(
            new AssertionSpec { Type = "natIs", Nodes = new() { "a" }, Text = "symmetric" },
            new AssertionSpec { Type = "relayCapable", Nodes = new() { "a" }, Value = 0 },
            new AssertionSpec { Type = "overServer", Nodes = new() { "b" } },
            new AssertionSpec { Type = "noOverServer", Nodes = new() { "a" } },
            new AssertionSpec { Type = "natIs", Nodes = new() { "c" }, Text = "symmetric" }), nodes);
        Assert.True(results[0].Passed, results[0].Message);
        Assert.True(results[1].Passed, results[1].Message);
        Assert.True(results[2].Passed, results[2].Message);
        Assert.True(results[3].Passed, results[3].Message);
        Assert.False(results[4].Passed);
    }

    [Fact]
    public void An_unknown_assertion_fails_loudly_instead_of_being_ignored()
    {
        var results = Analysis.Check(With(new AssertionSpec { Type = "worksOnMyMachine" }), Tree());
        var only = Assert.Single(results);
        Assert.False(only.Passed);
        Assert.Contains("unknown assertion type", only.Message);
    }

    [Fact]
    public void The_tree_is_read_from_the_last_parent_each_viewer_had_because_at_the_end_the_sharer_is_gone()
    {
        var nodes = Tree();
        // At the end of the run nobody has a parent any more; the last one they had is what counts.
        nodes[1] = Node("a", "watch", Hello("A"), Watcher("S").Replace("\"parent\":\"S\"", "\"parent\":null,\"lastParent\":\"S\"").Replace("\"attached\":true", "\"attached\":false"));
        nodes[2] = Node("b", "watch", Hello("B"), Watcher("A").Replace("\"parent\":\"A\"", "\"parent\":null,\"lastParent\":\"A\"").Replace("\"attached\":true", "\"attached\":false"));
        var depths = Analysis.Depths(nodes);
        Assert.Equal(1, depths["a"]);
        Assert.Equal(2, depths["b"]);
    }

    [Fact]
    public void Finding_the_others_is_judged_on_its_own_and_a_node_that_never_did_fails()
    {
        var nodes = Tree();
        nodes[1] = Node("a", "watch", Hello("A"), Watcher("S", extra: ",\"firstPeerAtMs\":4500"));
        nodes[2] = Node("b", "watch", Hello("B"), Watcher("A", extra: ",\"firstPeerAtMs\":75000"));
        nodes[3] = Node("c", "watch", Hello("C"), Watcher("B"));   // never reports one
        var results = Analysis.Check(With(new AssertionSpec { Type = "discoveryWithin", Value = 60, Nodes = new() { "a", "b", "c" } }), nodes);
        var only = Assert.Single(results);
        Assert.False(only.Passed);
        Assert.Contains("b found someone after 75.0 s", only.Message);
        Assert.Contains("c never found anyone", only.Message);
        Assert.DoesNotContain("a found", only.Message);

        var fine = Analysis.Check(With(new AssertionSpec { Type = "discoveryWithin", Value = 90, Nodes = new() { "a", "b" } }), nodes);
        Assert.True(Assert.Single(fine).Passed);
    }

    [Fact]
    public void A_viewer_that_never_saw_the_stream_fails_the_delivery_check_and_the_report_says_so()
    {
        var nodes = Tree();
        nodes[1] = Node("a", "watch", Hello("A"), """{"type":"summary","role":"watch","sawGroup":false,"waitedMs":55000}""");
        var scenario = With(new AssertionSpec { Type = "viewersReceive", Value = 0.9 });
        var checks = Analysis.Check(scenario, nodes);
        Assert.False(Assert.Single(checks).Passed);
        Assert.Contains("a got", checks[0].Message);
        Assert.Contains("never saw the stream", Report.Build(scenario, nodes, checks, "r", Array.Empty<string>()));
    }

    [Fact]
    public void The_report_names_the_parents_by_node_and_says_pass_or_fail()
    {
        var nodes = Tree();
        var scenario = With(new AssertionSpec { Type = "treeDepthAtLeast", Value = 3 });
        var checks = Analysis.Check(scenario, nodes);
        var report = Report.Build(scenario, nodes, checks, "run1", new[] { "a note" });
        Assert.Contains("**Result: PASS**", report);
        Assert.Contains("| c | watch |", report);
        Assert.Contains("| b | 3 |", report);       // c sits under b, at depth 3
        Assert.Contains("a note", report);

        var failing = Report.Build(scenario, nodes, Analysis.Check(With(new AssertionSpec { Type = "treeDepthAtLeast", Value = 9 }), nodes), "run2", Array.Empty<string>());
        Assert.Contains("**Result: FAIL**", failing);
    }
}
