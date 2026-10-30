using System.Globalization;
using System.Text;
using System.Text.Json;

/// <summary>Everything one agent printed, with its last summary line picked out.</summary>
internal sealed class NodeResult
{
    public NodeResult(NodeSpec spec) => Spec = spec;

    public NodeSpec Spec { get; }
    public string Name => Spec.Name;
    public string Role => Spec.Role;
    public List<JsonElement> Lines { get; } = new();
    public List<string> Raw { get; } = new();

    /// <summary>The scenario ended this node on purpose (kill), so it has no summary and that is fine.</summary>
    public bool Killed { get; set; }
    public int ExitCode { get; set; }

    public string? Id => Lines.FirstOrDefault(l => Type(l) == "hello") is { ValueKind: JsonValueKind.Object } h ? h.GetProperty("id").GetString() : null;
    public JsonElement? Summary => Lines.LastOrDefault(l => Type(l) == "summary") is { ValueKind: JsonValueKind.Object } s ? s : null;
    public string? Error => Lines.LastOrDefault(l => Type(l) == "error") is { ValueKind: JsonValueKind.Object } e ? e.GetProperty("message").GetString() : null;
    public bool Failed => !Killed && (Summary == null || Error != null);

    public void Add(string line)
    {
        Raw.Add(line);
        try { using var doc = JsonDocument.Parse(line); Lines.Add(doc.RootElement.Clone()); }
        catch (JsonException) { }
    }

    private static string? Type(JsonElement e) => e.TryGetProperty("type", out var t) ? t.GetString() : null;

    public double Num(string key, double fallback = 0) =>
        Summary is { } s && s.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    public bool Flag(string key) => Summary is { } s && s.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    public string? Str(string key) => Summary is { } s && s.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

internal sealed record AssertionResult(AssertionSpec Spec, bool Passed, string Message);

internal static class Analysis
{
    private static double Seconds(double ms) => ms / 1000.0;

    private static IEnumerable<NodeResult> Viewers(IReadOnlyList<NodeResult> nodes) => nodes.Where(n => n.Role == "watch" && !n.Killed);

    private static IEnumerable<NodeResult> Pick(IReadOnlyList<NodeResult> nodes, AssertionSpec a, Func<NodeResult, bool> byDefault) =>
        a.Nodes is { Count: > 0 } names ? nodes.Where(n => names.Contains(n.Name)) : nodes.Where(byDefault);

    /// <summary>Depth of each watcher in the tree: 1 for one directly under the sharer. Zero if it never attached.</summary>
    public static Dictionary<string, int> Depths(IReadOnlyList<NodeResult> nodes)
    {
        var byId = nodes.Where(n => n.Id != null).ToDictionary(n => n.Id!, n => n);
        var sharer = nodes.FirstOrDefault(n => n.Role == "share");
        var depths = new Dictionary<string, int>();
        foreach (var v in nodes.Where(n => n.Role == "watch"))
        {
            int depth = 0;
            var cur = v;
            for (int steps = 0; steps < 64; steps++)
            {
                // The last parent it had while the stream ran; at the very end the sharer has stopped and nobody has one.
                var parent = cur.Str("lastParent") ?? cur.Str("parent");
                if (parent == null || !(cur.Num("attachedAtMs", -1) >= 0 || cur.Flag("attached"))) { depth = 0; break; }
                depth++;
                if (sharer?.Id == parent) break;
                if (!byId.TryGetValue(parent, out var next)) { depth = 0; break; }
                cur = next;
            }
            depths[v.Name] = depth;
        }
        return depths;
    }

    public static List<AssertionResult> Check(Scenario scenario, IReadOnlyList<NodeResult> nodes)
    {
        var results = new List<AssertionResult>();
        var depths = Depths(nodes);

        AssertionResult Each(AssertionSpec a, IEnumerable<NodeResult> targets, Func<NodeResult, (bool Ok, string Detail)> test, string what)
        {
            var list = targets.ToList();
            if (list.Count == 0) return new(a, false, $"{what}: no nodes to check");
            var failures = list.Select(n => (n, r: test(n))).Where(x => !x.r.Ok).ToList();
            return failures.Count == 0
                ? new(a, true, $"{what}: " + string.Join(", ", list.Select(n => $"{n.Name} {test(n).Detail}")))
                : new(a, false, $"{what}: " + string.Join("; ", failures.Select(f => $"{f.n.Name} {f.r.Detail}")));
        }

        foreach (var a in scenario.Assertions)
        {
            switch (a.Type)
            {
                case "viewersReceive":
                    results.Add(Each(a, Pick(nodes, a, n => n.Role == "watch" && !n.Killed), n =>
                    {
                        double span = n.Num("span"), got = n.Num("distinct");
                        double fraction = span > 0 ? got / span : 0;
                        return (n.Flag("sawGroup") && fraction >= a.Value, $"got {got:0} of {span:0} frames ({fraction:P1})");
                    }, $"every viewer gets at least {a.Value:P0} of the frames between its first and last"));
                    break;

                case "discoveryWithin":
                    results.Add(Each(a, Pick(nodes, a, n => !n.Killed), n =>
                    {
                        double at = n.Num("firstPeerAtMs", -1);
                        return (at >= 0 && Seconds(at) <= a.Value, at >= 0 ? $"found someone after {Seconds(at):0.0} s" : "never found anyone");
                    }, $"found another member within {a.Value:0} s of starting"));
                    break;

                case "attachWithin":
                    results.Add(Each(a, Pick(nodes, a, n => n.Role == "watch" && !n.Killed), n =>
                    {
                        double took = Seconds(n.Num("attachedAtMs", -1) - n.Num("groupSeenAtMs"));
                        return (n.Num("attachedAtMs", -1) >= 0 && took <= a.Value, n.Num("attachedAtMs", -1) >= 0 ? $"attached after {took:0.0} s" : "never attached");
                    }, $"attached within {a.Value:0} s of seeing the stream"));
                    break;

                case "firstFrameWithin":
                    results.Add(Each(a, Pick(nodes, a, n => n.Role == "watch" && !n.Killed), n =>
                    {
                        double took = Seconds(n.Num("firstFrameAtMs", -1) - n.Num("groupSeenAtMs"));
                        return (n.Num("firstFrameAtMs", -1) >= 0 && took <= a.Value, n.Num("firstFrameAtMs", -1) >= 0 ? $"first frame after {took:0.0} s" : "no frame");
                    }, $"first frame within {a.Value:0} s of seeing the stream"));
                    break;

                case "treeDepthAtLeast":
                    {
                        int max = depths.Values.DefaultIfEmpty(0).Max();
                        results.Add(new(a, max >= a.Value, $"the tree is {max} deep (wanted at least {a.Value:0})"));
                        break;
                    }

                case "treeDepthAtMost":
                    {
                        int max = depths.Values.DefaultIfEmpty(0).Max();
                        results.Add(new(a, max <= a.Value, $"the tree is {max} deep (wanted at most {a.Value:0})"));
                        break;
                    }

                case "latencyP95Below":
                    results.Add(Each(a, Pick(nodes, a, n => n.Role == "watch" && !n.Killed), n =>
                        (n.Num("latencyP95Ms") < a.Value, $"p95 {n.Num("latencyP95Ms"):0} ms"), $"95th percentile delay below {a.Value:0} ms (clocks assumed within NTP accuracy)"));
                    break;

                case "maxFrameGapBelow":
                    results.Add(Each(a, Pick(nodes, a, n => n.Role == "watch" && !n.Killed), n =>
                        (Seconds(n.Num("maxFrameGapMs")) <= a.Value, $"longest gap {Seconds(n.Num("maxFrameGapMs")):0.0} s"), $"no gap between frames longer than {a.Value:0.0} s"));
                    break;

                case "overServer":
                    results.Add(Each(a, Pick(nodes, a, _ => false), n => (n.Flag("overServer") && !n.Flag("attached"), n.Flag("overServer") ? "refused: too big for the shared server" : "was not refused"), "refused because the stream is too big for the TURN server"));
                    break;

                case "noOverServer":
                    results.Add(Each(a, Pick(nodes, a, n => n.Role == "watch" && !n.Killed), n => (!n.Flag("overServer"), n.Flag("overServer") ? "was refused" : "fine"), "not refused for the TURN server"));
                    break;

                case "retransmitsAtLeast":
                    {
                        double total = nodes.Sum(n => n.Num("retransmitted"));
                        results.Add(new(a, total >= a.Value, $"{total:0} packets were sent again (wanted at least {a.Value:0})"));
                        break;
                    }

                case "natIs":
                    results.Add(Each(a, Pick(nodes, a, _ => false), n => (string.Equals(n.Str("nat"), a.Text, StringComparison.OrdinalIgnoreCase), $"reports {n.Str("nat") ?? "nothing"}"), $"detected as {a.Text}"));
                    break;

                case "relayCapable":
                    results.Add(Each(a, Pick(nodes, a, _ => false), n => (n.Flag("relayCapable") == (a.Value != 0), n.Flag("relayCapable") ? "can relay" : "cannot relay"), a.Value != 0 ? "can relay" : "cannot relay"));
                    break;

                case "parentChangesAtLeast":
                    results.Add(Each(a, Pick(nodes, a, _ => false), n => (n.Num("parentChanges") >= a.Value, $"changed parent {n.Num("parentChanges"):0} times"), $"changed parent at least {a.Value:0} times"));
                    break;

                case "voiceHeard":
                    results.Add(Each(a, Pick(nodes, a, n => n.Role == "voice" && !n.Killed), n =>
                        (n.Num("heardFrames") >= a.Value, $"heard sound in {n.Num("heardFrames"):0} frames"), $"heard at least {a.Value:0} frames of sound"));
                    break;

                case "voiceTreeUsed":
                    {
                        var voice = nodes.Where(n => n.Role == "voice" && !n.Killed).ToList();
                        int roots = voice.Count(n => n.Flag("isRoot")), onTree = voice.Count(n => n.Flag("tree"));
                        results.Add(new(a, roots == 1 && onTree == voice.Count, $"{onTree} of {voice.Count} are on the voice tree, {roots} root(s)"));
                        break;
                    }

                default:
                    results.Add(new(a, false, $"unknown assertion type '{a.Type}'"));
                    break;
            }
        }

        foreach (var n in nodes.Where(n => n.Failed))
            results.Add(new(new AssertionSpec { Type = "noErrors" }, false, $"{n.Name}: {(n.Error ?? "the agent ended without a summary")} (exit code {n.ExitCode})"));
        return results;
    }
}

internal static class Report
{
    private static string Found(NodeResult n) =>
        n.Num("firstPeerAtMs", -1) >= 0 ? (n.Num("firstPeerAtMs") / 1000).ToString("0.0", CultureInfo.InvariantCulture) : "never";

    public static string Build(Scenario scenario, IReadOnlyList<NodeResult> nodes, IReadOnlyList<AssertionResult> checks, string runId, IReadOnlyList<string> notes)
    {
        var depths = Analysis.Depths(nodes);
        var byId = nodes.Where(n => n.Id != null).ToDictionary(n => n.Id!, n => n.Name);
        var sb = new StringBuilder();
        sb.AppendLine($"# {scenario.Name}  ({runId})");
        if (scenario.Description.Length > 0) sb.AppendLine().AppendLine(scenario.Description);
        sb.AppendLine();
        bool pass = checks.All(c => c.Passed);
        sb.AppendLine(pass ? "**Result: PASS**" : "**Result: FAIL**").AppendLine();

        sb.AppendLine("| Node | Role | Region | NAT set / found | Found a peer s | Parent | Depth | Frames | Kept | Attach s | First frame s | Delay p50 / p95 ms | Longest gap s | Resent / recovered |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var n in nodes)
        {
            string parent = (n.Str("lastParent") ?? n.Str("parent")) is { } p && byId.TryGetValue(p, out var pn) ? pn : "-";
            string nat = $"{n.Spec.Nat} / {n.Str("nat") ?? "?"}{(n.Flag("relayCapable") ? " (relays)" : "")}";
            if (n.Role == "watch")
            {
                double span = n.Num("span"), got = n.Num("distinct");
                string attach = n.Num("attachedAtMs", -1) >= 0 ? ((n.Num("attachedAtMs") - n.Num("groupSeenAtMs")) / 1000).ToString("0.0", CultureInfo.InvariantCulture) : "never";
                string first = n.Num("firstFrameAtMs", -1) >= 0 ? ((n.Num("firstFrameAtMs") - n.Num("groupSeenAtMs")) / 1000).ToString("0.0", CultureInfo.InvariantCulture) : "none";
                sb.AppendLine($"| {n.Name}{(n.Killed ? " (killed)" : "")}{(n.Flag("overServer") ? " (too big for TURN)" : "")}{(n.Summary == null || n.Flag("sawGroup") ? "" : " (never saw the stream)")} | watch | {n.Spec.Region} | {nat} | {Found(n)} | {parent} | {depths.GetValueOrDefault(n.Name)} | {got:0}/{span:0} | {(span > 0 ? (got / span).ToString("P0") : "-")} | {attach} | {first} | {n.Num("latencyP50Ms"):0} / {n.Num("latencyP95Ms"):0} | {n.Num("maxFrameGapMs") / 1000:0.0} | {n.Num("retransmitted"):0} / {n.Num("recovered"):0} |");
            }
            else if (n.Role == "share")
                sb.AppendLine($"| {n.Name} | share | {n.Spec.Region} | {nat} | {Found(n)} | - | 0 | {n.Num("frames"):0} sent | - | - | - | - | - | {n.Num("retransmitted"):0} / - |");
            else
                sb.AppendLine($"| {n.Name} | voice{(n.Flag("speaker") ? " (speaks)" : "")} | {n.Spec.Region} | {nat} | {Found(n)} | {parent} | - | heard {n.Num("heardFrames"):0} | - | {(n.Num("treeAttachedAtMs", -1) >= 0 ? (n.Num("treeAttachedAtMs") / 1000).ToString("0.0", CultureInfo.InvariantCulture) : "-")} | - | - | - | - |");
        }

        sb.AppendLine().AppendLine("## Checks").AppendLine();
        foreach (var c in checks) sb.AppendLine($"- {(c.Passed ? "PASS" : "**FAIL**")}  {c.Message}");
        if (checks.Count == 0) sb.AppendLine("- (the scenario has no assertions)");
        if (notes.Count > 0)
        {
            sb.AppendLine().AppendLine("## Notes").AppendLine();
            foreach (var note in notes) sb.AppendLine("- " + note);
        }
        return sb.ToString();
    }
}
