using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

internal sealed class RunOptions
{
    /// <summary>Run every node as a process on this computer instead of creating machines. For trying a scenario out.</summary>
    public bool Local { get; set; }
    public bool DryRun { get; set; }

    /// <summary>Leave the machines running afterwards, for looking around. You pay for them until you run cleanup.</summary>
    public bool Keep { get; set; }
    public string ResultsRoot { get; set; } = "results";
}

internal sealed class Runner
{
    private readonly Scenario _scenario;
    private readonly Settings _settings;
    private readonly RunOptions _options;
    private readonly List<string> _notes = new();
    private readonly string _runId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    private readonly Dictionary<string, Machine> _machines = new();
    private readonly Dictionary<string, RunningAgent> _agents = new();
    private readonly List<NodeResult> _results = new();
    private readonly List<(Machine Machine, string Ip)> _blocks = new();
    private StreamWriter? _logFile;
    private string _turnPassword = "";
    private string _turnAddress = "";
    private DigitalOcean? _api;

    public Runner(Scenario scenario, Settings settings, RunOptions options)
    {
        _scenario = scenario;
        _settings = settings;
        _options = options;
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Console.WriteLine(line);
        lock (this) _logFile?.WriteLine(line);
    }

    public static string? RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Yaparr.slnx"))) return dir.FullName;
        return null;
    }

    // ---- the run ------------------------------------------------------------------------------

    public async Task<int> RunAsync(CancellationToken ct)
    {
        string dir = Path.Combine(_options.ResultsRoot, $"{_runId}-{_scenario.Name}");
        var key = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

        if (_options.DryRun && !_options.Local) { PrintPlan(key); return 0; }

        Directory.CreateDirectory(dir);
        _logFile = new StreamWriter(Path.Combine(dir, "run.log"), append: false) { AutoFlush = true };
        foreach (var n in _scenario.Nodes) _results.Add(new NodeResult(n));
        Log($"Scenario '{_scenario.Name}' ({_scenario.Nodes.Count} nodes, up to {_scenario.SettleSeconds} s to find each other, then {_scenario.DurationSeconds} s). Results go to {dir}");

        try
        {
            string agent = await PrepareAgentAsync(ct);
            await ProvisionAsync(agent, ct);
            await SetUpNetworkAsync(ct);
            await RunAgentsAsync(key, ct);
        }
        catch (OperationCanceledException) { _notes.Add("The run was cancelled."); }
        catch (Exception ex)
        {
            Log("The run failed: " + ex.Message);
            _notes.Add("The run failed: " + ex.Message);
        }
        finally
        {
            await CleanUpAsync();
        }

        foreach (var r in _results) File.WriteAllLines(Path.Combine(dir, r.Name + ".jsonl"), r.Raw);
        var checks = Analysis.Check(_scenario, _results);
        if (_notes.Any(n => n.StartsWith("The run failed")) || _results.All(r => r.Raw.Count == 0))
            checks.Add(new AssertionResult(new AssertionSpec { Type = "completed" }, false, "the run did not complete"));
        var report = Report.Build(_scenario, _results, checks, _runId, _notes);
        File.WriteAllText(Path.Combine(dir, "report.md"), report);
        Console.WriteLine();
        Console.WriteLine(report);
        Log($"Report: {Path.Combine(dir, "report.md")}");
        lock (this) { _logFile?.Dispose(); _logFile = null; }
        return checks.All(c => c.Passed) ? 0 : 1;
    }

    private void PrintPlan(string key)
    {
        Console.WriteLine($"Scenario '{_scenario.Name}': {_scenario.Description}");
        Console.WriteLine($"Up to {_scenario.SettleSeconds} s for the machines to find each other, then {_scenario.DurationSeconds} s of the stream.");
        Console.WriteLine($"Would create {_scenario.Nodes.Count + (_scenario.Turn ? 1 : 0)} machines ({_settings.Size}, {_settings.Image}) tagged '{_settings.Tag}':");
        foreach (var n in _scenario.Nodes)
            Console.WriteLine($"  {n.Name,-10} {n.Region,-5} role={n.Role,-5} nat={n.Nat,-9} start at +{n.StartAfterSeconds}s  agent: {string.Join(' ', AgentArgs(n, key, 0, Lifetime(n)))}");
        if (_scenario.Turn) Console.WriteLine("  turn       (a coturn server on its own machine)");
        Console.WriteLine("Timeline:");
        foreach (var t in _scenario.Timeline.OrderBy(t => t.AtSeconds))
            Console.WriteLine($"  +{t.AtSeconds,3}s {t.Action,-11} {t.Node}{(t.Other != null ? " vs " + t.Other : "")}{(t.Args != null ? " " + t.Args : "")}");
        Console.WriteLine("Checks:");
        foreach (var a in _scenario.Assertions) Console.WriteLine($"  {a.Type} {(a.Nodes != null ? string.Join(",", a.Nodes) : "")} {a.Value}{a.Text}");
        Console.WriteLine($"First-start script for a node:\n{Scripts.CloudInit(false, "")}");
        Console.WriteLine("Nothing was created. Run again without --dry-run (and with DIGITALOCEAN_TOKEN set) to do it for real.");
    }

    // ---- getting the agent and the machines ready --------------------------------------------

    private async Task<string> PrepareAgentAsync(CancellationToken ct)
    {
        var root = RepoRoot() ?? throw new InvalidOperationException("Run the tool from inside the Yaparr repository.");
        var project = Path.Combine(root, "tools", "Yaparr.NetAgent", "Yaparr.NetAgent.csproj");
        if (_options.Local)
        {
            Log("Building the agent...");
            await Dotnet($"build \"{project}\" -c Release -v q --nologo", ct);
            var exe = Path.Combine(root, "tools", "Yaparr.NetAgent", "bin", "Release", "net10.0", OperatingSystem.IsWindows() ? "Yaparr.NetAgent.exe" : "Yaparr.NetAgent");
            return exe;
        }

        Log("Publishing the agent for Linux...");
        string publishDir = Path.Combine(Path.GetTempPath(), "yaparr-nettest-publish-" + _runId);
        await Dotnet($"publish \"{project}\" -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o \"{publishDir}\" -v q --nologo", ct);
        string tar = publishDir + ".tar";
        string archive = publishDir + ".tar.gz";
        TarFile.CreateFromDirectory(publishDir, tar, includeBaseDirectory: false);
        await using (var input = File.OpenRead(tar))
        await using (var output = File.Create(archive))
        await using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
            await input.CopyToAsync(gzip, ct);
        File.Delete(tar);
        Log($"Agent package: {new FileInfo(archive).Length / 1024 / 1024} MB");
        return archive;
    }

    private static async Task Dotnet(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("dotnet", arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync(ct);
        var error = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"dotnet {arguments.Split(' ')[0]} failed:\n{await output}\n{await error}");
    }

    private async Task ProvisionAsync(string agent, CancellationToken ct)
    {
        if (_options.Local)
        {
            foreach (var n in _scenario.Nodes) _machines[n.Name] = new LocalMachine(n.Name, agent);
            return;
        }

        _api = new DigitalOcean(_settings.Token);
        var (limit, email) = await _api.AccountAsync(ct);
        var existing = await _api.ListByTagAsync(_settings.Tag, ct);
        int need = _scenario.Nodes.Count + (_scenario.Turn ? 1 : 0);
        Log($"DigitalOcean account {email}: limit {limit} droplets, {existing.Count} tagged '{_settings.Tag}' already running.");
        if (existing.Count > 0) _notes.Add($"{existing.Count} machine(s) tagged '{_settings.Tag}' were already running before this run (from an earlier one?). Run 'cleanup' to remove them.");
        if (need > _settings.MaxDroplets) throw new InvalidOperationException($"This scenario needs {need} machines but maxDroplets is {_settings.MaxDroplets}.");
        if (existing.Count + need > limit) throw new InvalidOperationException($"Your account allows {limit} droplets and {existing.Count} are in use; this needs {need} more.");
        if (!File.Exists(_settings.SshKeyFile)) throw new InvalidOperationException($"The SSH key file '{_settings.SshKeyFile}' (sshKeyFile in nettest.json) does not exist.");
        long keyId = await _api.FindKeyAsync(_settings.SshKeyName, ct);

        _turnPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant();
        string runTag = "yaparr-run-" + _runId;
        var tags = new[] { _settings.Tag, runTag };

        DigitalOcean.SnapshotInfo? snapshot = null;
        if (_settings.SnapshotName.Length > 0)
        {
            snapshot = await _api.FindSnapshotAsync(_settings.SnapshotName, ct) ?? await _api.FindSnapshotAsync(_settings.SnapshotName + "-new", ct);
            Log(snapshot == null
                ? $"No snapshot named '{_settings.SnapshotName}': machines install their tools at first start. Run 'bake' once to make starts faster."
                : $"Snapshot '{snapshot.Name}' is available in {string.Join(", ", snapshot.Regions)}.");
        }

        var bakedNames = new HashSet<string>();
        Log($"Creating {need} machines...");
        var created = new List<(string Name, Task<DigitalOcean.DropletInfo> Task, bool Turn)>();
        foreach (var n in _scenario.Nodes)
            created.Add((n.Name, CreateAsync($"yaparr-nt-{_runId}-{n.Name}", n.Region, false), false));
        if (_scenario.Turn) created.Add(("turn", CreateAsync($"yaparr-nt-{_runId}-turn", _scenario.Nodes[0].Region, true), true));

        async Task<DigitalOcean.DropletInfo> CreateAsync(string name, string region, bool turn)
        {
            bool baked = snapshot != null && snapshot.Regions.Contains(region);
            if (baked) lock (bakedNames) bakedNames.Add(name);
            if (snapshot != null && !baked) Log($"  the snapshot is not in {region}; {name} installs its tools at first start (bake with that region to fix).");
            var d = await _api!.CreateAsync(name, region, _settings.Size, baked ? snapshot!.Id : _settings.Image, keyId, tags,
                Scripts.CloudInit(turn, _turnPassword, _scenario.Timeline.Any(t => t.Action == "netem"), baked), ct);
            return await _api.WaitActiveAsync(d.Id, ct);
        }

        var all = new List<Machine>();
        foreach (var (name, task, turn) in created)
        {
            var d = await task;
            var machine = new SshMachine(turn ? "turn" : name, d.Ip!, _settings.SshUser, _settings.SshKeyFile);
            if (bakedNames.Contains(d.Name)) machine.ReadyScript = Scripts.BakedReady(turn, _scenario.Timeline.Any(t => t.Action == "netem"));
            all.Add(machine);
            if (turn) { _turnAddress = $"{d.Ip}:3478"; _machines["turn"] = machine; }
            else _machines[name] = machine;
            Log($"  {machine.Name}: {d.Ip} ({d.Region})");
        }

        Log("Waiting for the machines to answer and finish setting themselves up...");
        using var ready = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? firstFailure = null;
        var readyNames = new HashSet<string>();
        await Task.WhenAll(all.Select(async m =>
        {
            try { await m.WaitReadyAsync(ready.Token, Log); lock (readyNames) readyNames.Add(m.Name); }
            catch (Exception ex)
            {
                // The first real failure is the cause; the others only stop because of it.
                if (Interlocked.CompareExchange(ref firstFailure, ex, null) == null) ready.Cancel();
            }
        }));
        if (firstFailure != null && !ct.IsCancellationRequested)
        {
            Log("These machines were not ready; checking why:");
            await Task.WhenAll(all.Where(m => !readyNames.Contains(m.Name)).Select(async m =>
                Log($"  {m.Name} ({m.Address}): {await m.DiagnoseAsync(ct)}")));
        }
        if (firstFailure != null && !ct.IsCancellationRequested) throw new InvalidOperationException(firstFailure.Message, firstFailure);
        ct.ThrowIfCancellationRequested();
        Log("Copying the agent...");
        await Task.WhenAll(all.Where(m => m.Name != "turn").Select(m => m.DeployAsync(agent, ct)));
    }

    private async Task SetUpNetworkAsync(CancellationToken ct)
    {
        if (_options.Local) return;
        var jobs = new List<Task>();
        foreach (var n in _scenario.Nodes)
        {
            var m = _machines[n.Name];
            if (n.Nat != "none") jobs.Add(Task.Run(async () => { await m.RunScriptAsync(Scripts.Nat(n.Nat, _settings.UdpPort), ct); Log($"  {n.Name} is behind a {n.Nat} NAT"); }, ct));
        }
        var shaped = _scenario.Timeline.Where(t => t.Action == "netem").Select(t => t.Node).Distinct();
        foreach (var name in shaped)
        {
            var m = _machines[name];
            jobs.Add(Task.Run(async () =>
            {
                try { await m.RunAsync("modprobe sch_netem && tc qdisc show >/dev/null", TimeSpan.FromSeconds(30), ct); }
                catch (InvalidOperationException) { throw new InvalidOperationException($"{name}: this machine cannot shape traffic (the sch_netem module is missing)."); }
            }, ct));
        }
        await Task.WhenAll(jobs);
    }

    // ---- running ------------------------------------------------------------------------------

    // Settling, then the stream, then some room to finish; counted from when this agent starts.
    private int Lifetime(NodeSpec node) => _scenario.SettleSeconds + _scenario.DurationSeconds + 30 - node.StartAfterSeconds;

    private string[] AgentArgs(NodeSpec node, string key, int index, int duration)
    {
        var args = new List<string>
        {
            "--role", node.Role, "--room", key, "--name", node.Name,
            "--port", (_options.Local ? _settings.UdpPort + index : _settings.UdpPort).ToString(), "--duration", Math.Max(5, duration).ToString(),
        };
        if (node.Role == "share" && !node.Args.ContainsKey("wait-peers"))
        {
            // Share once the others are found, for as long as the scenario says.
            args.AddRange(new[] { "--wait-peers", (_scenario.Nodes.Count - 1).ToString(), "--wait-peers-timeout", _scenario.SettleSeconds.ToString(), "--stream-seconds", _scenario.DurationSeconds.ToString() });
        }
        if (_options.Local)
        {
            args.Add("--local");
            if (node.Nat == "none" && !node.Args.ContainsKey("relay-capable")) { args.Add("--relay-capable"); args.Add("true"); }
        }
        if (_scenario.Turn && !_options.Local) { args.AddRange(new[] { "--turn", _turnAddress, "--turn-user", "nettest", "--turn-pass", _turnPassword }); }
        foreach (var (k, v) in node.Args)
        {
            args.Add("--" + k);
            var text = v is JsonElement e ? (e.ValueKind == JsonValueKind.True ? "true" : e.ValueKind == JsonValueKind.False ? "false" : e.ToString()) : v.ToString()!;
            if (text != "true" || k is "relay-capable") args.Add(text);
        }
        return args.ToArray();
    }

    private async Task RunAgentsAsync(string key, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        // The timeline counts from the moment sharing starts (or, with no sharer, from the end of the settle time).
        var streamStart = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_scenario.Nodes.Any(n => n.Role == "share"))
            _ = Task.Delay(TimeSpan.FromSeconds(_scenario.SettleSeconds), CancellationToken.None).ContinueWith(_ => streamStart.TrySetResult(clock.ElapsedMilliseconds));
        var starts = new List<Task>();
        for (int i = 0; i < _scenario.Nodes.Count; i++)
        {
            int index = i;
            var node = _scenario.Nodes[i];
            starts.Add(Task.Run(async () =>
            {
                var wait = TimeSpan.FromSeconds(node.StartAfterSeconds) - clock.Elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                var machine = _machines[node.Name];
                var result = _results.First(r => r.Name == node.Name);
                var running = machine.StartAgent(AgentArgs(node, key, index, Lifetime(node)), node.Nat != "none" && !_options.Local, line =>
                {
                    lock (result)
                    {
                        result.Add(line);
                        if (line.Contains("\"type\":\"hello\"") && TryPid(line, out var pid)) machine.AgentPid = pid;
                    }
                    if (node.Role == "share" && line.Contains("\"type\":\"sharing\"")) { streamStart.TrySetResult(clock.ElapsedMilliseconds); Log("  sharing has started"); }
                });
                lock (_agents) _agents[node.Name] = running;
                Log($"  started {node.Name} ({node.Role}, {node.Region}{(node.Nat != "none" ? ", " + node.Nat + " NAT" : "")})");
            }, ct));
        }

        var timeline = Task.Run(async () =>
        {
            if (_scenario.Timeline.Count == 0) return;
            long origin;
            try { origin = await streamStart.Task.WaitAsync(TimeSpan.FromSeconds(_scenario.SettleSeconds + 60), ct); }
            catch (TimeoutException) { _notes.Add("Sharing never started, so the timeline did not run."); return; }
            foreach (var entry in _scenario.Timeline.OrderBy(t => t.AtSeconds))
            {
                var wait = TimeSpan.FromMilliseconds(origin) + TimeSpan.FromSeconds(entry.AtSeconds) - clock.Elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                await DoAsync(entry, ct);
            }
        }, ct);

        await Task.WhenAll(starts);
        Log("All agents are running.");
        await timeline;

        // They stop by themselves at the end of their time; allow a little extra for the last summary lines.
        var end = TimeSpan.FromSeconds(_scenario.SettleSeconds + _scenario.DurationSeconds + 60);
        List<Task> waits;
        lock (_agents) waits = _agents.Values.Select(a => a.Exited).ToList();
        var all = Task.WhenAll(waits);
        var left = end - clock.Elapsed;
        if (left > TimeSpan.Zero) await Task.WhenAny(all, Task.Delay(left, ct));
        if (!all.IsCompleted) _notes.Add("Some agents did not finish in time and were stopped.");
        foreach (var r in _results) { lock (_agents) if (_agents.TryGetValue(r.Name, out var a)) r.ExitCode = a.ExitCode; }
    }

    private static bool TryPid(string line, out int pid)
    {
        pid = 0;
        try { using var doc = JsonDocument.Parse(line); return doc.RootElement.TryGetProperty("pid", out var p) && p.TryGetInt32(out pid); }
        catch (JsonException) { return false; }
    }

    private async Task DoAsync(TimelineEntry entry, CancellationToken ct)
    {
        var machine = _machines[entry.Node];
        try
        {
            Log($"  +{entry.AtSeconds}s {entry.Action} {entry.Node}{(entry.Other != null ? " vs " + entry.Other : "")}{(entry.Args != null ? " " + entry.Args : "")}");
            switch (entry.Action)
            {
                case "kill":
                    _results.First(r => r.Name == entry.Node).Killed = true;
                    if (machine.IsLocal) { lock (_agents) _agents[entry.Node].Kill(); }
                    else await machine.RunAsync($"kill -9 {machine.AgentPid}", TimeSpan.FromSeconds(20), ct);
                    break;
                case "freeze": await Needs(machine).RunAsync($"kill -STOP {machine.AgentPid}", TimeSpan.FromSeconds(20), ct); break;
                case "thaw": await Needs(machine).RunAsync($"kill -CONT {machine.AgentPid}", TimeSpan.FromSeconds(20), ct); break;
                case "netem": await Needs(machine).RunScriptAsync(Scripts.NetemOn(entry.Args!), ct); break;
                case "netem-clear": await Needs(machine).RunScriptAsync(Scripts.NetemOff(), ct); break;
                case "block":
                    {
                        var other = _machines[entry.Other ?? throw new InvalidOperationException("block needs 'other'")];
                        await Needs(machine).RunScriptAsync(Scripts.Block(other.Address!, add: true), ct);
                        _blocks.Add((machine, other.Address!));
                        break;
                    }
                case "unblock":
                    {
                        var other = _machines[entry.Other ?? throw new InvalidOperationException("unblock needs 'other'")];
                        await Needs(machine).RunScriptAsync(Scripts.Block(other.Address!, add: false), ct);
                        _blocks.RemoveAll(b => b.Machine == machine && b.Ip == other.Address);
                        break;
                    }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _notes.Add($"The timeline step '{entry.Action} {entry.Node}' at +{entry.AtSeconds}s failed: {ex.Message}");
            Log("    that step failed: " + ex.Message);
        }
    }

    private static Machine Needs(Machine machine) =>
        machine.IsLocal ? throw new NotSupportedException("this step needs a real machine; local runs only support 'kill'") : machine;

    // ---- afterwards ---------------------------------------------------------------------------

    private async Task CleanUpAsync()
    {
        lock (_agents) foreach (var agent in _agents.Values) agent.Kill();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        if (!_options.Local)
        {
            foreach (var (machine, ip) in _blocks.ToList())
            {
                try { await machine.RunScriptAsync(Scripts.Block(ip, add: false), cts.Token); } catch (Exception) { }
            }
        }

        if (_api == null) return;
        try
        {
            if (_options.Keep)
            {
                foreach (var machine in _machines.Values.Where(m => !m.IsLocal))
                {
                    try { await machine.RunAsync("pkill -f Yaparr.NetAgent || true", TimeSpan.FromSeconds(20), cts.Token); } catch (Exception) { }
                }
            }
            if (_options.Keep) Log($"Leaving the machines running (tag yaparr-run-{_runId}). Remove them with: Yaparr.NetTest cleanup");
            else
            {
                Log("Deleting the machines...");
                await _api.DeleteByTagAsync("yaparr-run-" + _runId, cts.Token);
                var left = await _api.ListByTagAsync("yaparr-run-" + _runId, cts.Token);
                if (left.Count > 0) _notes.Add($"{left.Count} machine(s) may still exist; run 'cleanup'.");
            }
        }
        catch (Exception ex)
        {
            Log("Could not delete the machines: " + ex.Message);
            _notes.Add("Could not delete the machines: " + ex.Message + " Run 'cleanup' to remove them.");
        }
        finally { _api.Dispose(); }
    }
}
