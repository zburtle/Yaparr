using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>What the tool needs to know about your account and machine. Read from nettest.json next to where you run it.</summary>
internal sealed class Settings
{
    /// <summary>The private key whose public half is registered with DigitalOcean.</summary>
    public string SshKeyFile { get; set; } = "";

    /// <summary>The name that key has in your DigitalOcean account (Settings, Security).</summary>
    public string SshKeyName { get; set; } = "";

    public string Size { get; set; } = "s-1vcpu-1gb";
    public string Image { get; set; } = "ubuntu-24-04-x64";
    public string SshUser { get; set; } = "root";

    /// <summary>The tool refuses to create more machines than this in one run.</summary>
    public int MaxDroplets { get; set; } = 10;

    public int UdpPort { get; set; } = 41000;

    /// <summary>Everything the tool creates carries this tag, so that cleanup can find it.</summary>
    public string Tag { get; set; } = "yaparr-nettest";

    public string Token => Environment.GetEnvironmentVariable("DIGITALOCEAN_TOKEN") ?? "";

    public static Settings Load(string? path)
    {
        path ??= File.Exists("nettest.json") ? "nettest.json" : null;
        if (path == null) return new Settings();
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Scenario.JsonOptions) ?? new Settings();
        if (settings.SshKeyFile.StartsWith("~")) settings.SshKeyFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), settings.SshKeyFile[2..]);
        return settings;
    }
}

internal sealed class Scenario
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>How long streaming (or the voice call) goes on, counted from the moment sharing starts.</summary>
    public int DurationSeconds { get; set; } = 60;

    /// <summary>
    /// The most time the machines get to find each other before sharing starts. On the real DHT a machine that has just
    /// been created can take a minute or more to find the others, and that is measured separately from the stream.
    /// </summary>
    public int SettleSeconds { get; set; } = 90;

    /// <summary>Start a TURN server (coturn) on its own machine and give the agents its address.</summary>
    public bool Turn { get; set; }

    public List<NodeSpec> Nodes { get; set; } = new();
    public List<TimelineEntry> Timeline { get; set; } = new();
    public List<AssertionSpec> Assertions { get; set; } = new();

    public static Scenario Load(string path)
    {
        var scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("The scenario file is empty.");
        scenario.Validate();
        return scenario;
    }

    private static readonly Regex Safe = new(@"^[A-Za-z0-9._:/=@,+-]+$");

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("The scenario needs a name.");
        if (Nodes.Count == 0) throw new InvalidDataException("The scenario has no nodes.");
        if (Nodes.Select(n => n.Name).Distinct().Count() != Nodes.Count) throw new InvalidDataException("Node names must be different.");
        if (Nodes.Count(n => n.Role == "share") > 1) throw new InvalidDataException("A scenario has at most one sharer.");
        foreach (var n in Nodes)
        {
            if (!Safe.IsMatch(n.Name)) throw new InvalidDataException($"The node name '{n.Name}' may only use letters, digits and . _ - characters.");
            if (n.Role is not ("share" or "watch" or "voice")) throw new InvalidDataException($"Node {n.Name}: the role must be share, watch or voice.");
            if (n.Nat is not ("none" or "cone" or "fullcone" or "symmetric")) throw new InvalidDataException($"Node {n.Name}: nat must be none, cone, fullcone or symmetric.");
            foreach (var (key, value) in n.Args)
                if (!Safe.IsMatch(key) || !Safe.IsMatch(value.ToString() ?? "")) throw new InvalidDataException($"Node {n.Name}: the argument {key} has characters that are not allowed.");
        }
        foreach (var t in Timeline)
        {
            if (t.Action is not ("kill" or "freeze" or "thaw" or "netem" or "netem-clear" or "block" or "unblock")) throw new InvalidDataException($"Unknown timeline action '{t.Action}'.");
            if (!Nodes.Any(n => n.Name == t.Node)) throw new InvalidDataException($"The timeline names a node that does not exist: {t.Node}.");
            if (t.Other != null && !Nodes.Any(n => n.Name == t.Other)) throw new InvalidDataException($"The timeline names a node that does not exist: {t.Other}.");
            if (t.Action == "netem" && !Regex.IsMatch(t.Args ?? "", @"^[A-Za-z0-9. %]+$")) throw new InvalidDataException("netem arguments may only use letters, digits, spaces, dots and %.");
        }
    }
}

internal sealed class NodeSpec
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "watch";

    /// <summary>A DigitalOcean region such as nyc3, sfo3, ams3, fra1, lon1, sgp1, syd1, tor1, blr1.</summary>
    public string Region { get; set; } = "nyc3";

    /// <summary>none (public address), cone (an ordinary home router), fullcone (a router with a port forwarded) or symmetric.</summary>
    public string Nat { get; set; } = "none";

    public int StartAfterSeconds { get; set; }

    /// <summary>Extra agent arguments, for example { "kbps": 1500, "fanout": 1 }.</summary>
    public Dictionary<string, object> Args { get; set; } = new();
}

internal sealed class TimelineEntry
{
    public int AtSeconds { get; set; }

    /// <summary>kill, freeze, thaw, netem, netem-clear, block or unblock.</summary>
    public string Action { get; set; } = "";
    public string Node { get; set; } = "";

    /// <summary>For block and unblock: the machine to cut off from the node.</summary>
    public string? Other { get; set; }

    /// <summary>For netem: what to do to the network, such as "loss 3% delay 80ms".</summary>
    public string? Args { get; set; }
}

internal sealed class AssertionSpec
{
    public string Type { get; set; } = "";
    public List<string>? Nodes { get; set; }
    public double Value { get; set; }
    public string? Text { get; set; }
}
