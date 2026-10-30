using System.Diagnostics;
using System.Text;

/// <summary>A running program on a machine whose output we read line by line.</summary>
internal sealed class RunningAgent : IDisposable
{
    private readonly Process _process;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RunningAgent(Process process, Action<string> onLine)
    {
        _process = process;
        process.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine("{\"type\":\"stderr\",\"msg\":" + System.Text.Json.JsonSerializer.Serialize(e.Data) + "}"); };
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => _exited.TrySetResult();
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public Task Exited => _exited.Task;
    public int ExitCode => _process.HasExited ? _process.ExitCode : -1;

    public void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    public void Dispose() { Kill(); _process.Dispose(); }
}

internal abstract class Machine
{
    protected Machine(string name) => Name = name;

    public string Name { get; }
    public string? Address { get; set; }
    public virtual bool IsLocal => false;

    /// <summary>Process id of the agent as it said in its first line, for kill and freeze.</summary>
    public int AgentPid { get; set; }

    public abstract Task<string> RunAsync(string shellCommand, TimeSpan timeout, CancellationToken ct);
    public abstract Task WaitReadyAsync(CancellationToken ct, Action<string> log);
    public abstract Task DeployAsync(string archive, CancellationToken ct);
    public abstract RunningAgent StartAgent(string[] agentArgs, bool behindNat, Action<string> onLine);

    /// <summary>Runs a script that needs a shell and root: NAT, traffic shaping.</summary>
    public Task<string> RunScriptAsync(string script, CancellationToken ct) =>
        RunAsync("echo " + Convert.ToBase64String(Encoding.UTF8.GetBytes(script)) + " | base64 -d | sh -e", TimeSpan.FromSeconds(60), ct);
}

/// <summary>This computer. Used to try scenarios without any cloud: no NAT, no traffic shaping.</summary>
internal sealed class LocalMachine : Machine
{
    private readonly string _agentPath;

    public LocalMachine(string name, string agentPath) : base(name) => _agentPath = agentPath;

    public override bool IsLocal => true;

    public override Task<string> RunAsync(string shellCommand, TimeSpan timeout, CancellationToken ct) =>
        throw new NotSupportedException("A local machine cannot run network scripts.");

    public override Task WaitReadyAsync(CancellationToken ct, Action<string> log) => Task.CompletedTask;
    public override Task DeployAsync(string archive, CancellationToken ct) => Task.CompletedTask;

    public override RunningAgent StartAgent(string[] agentArgs, bool behindNat, Action<string> onLine)
    {
        var psi = new ProcessStartInfo(_agentPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in agentArgs) psi.ArgumentList.Add(a);
        return new RunningAgent(new Process { StartInfo = psi }, onLine);
    }
}

/// <summary>A machine reached with the ssh and scp that come with Windows, macOS and Linux.</summary>
internal sealed class SshMachine : Machine
{
    private readonly string _user, _keyFile;

    public SshMachine(string name, string address, string user, string keyFile) : base(name)
    {
        Address = address;
        _user = user;
        _keyFile = keyFile;
    }

    private static string NullDevice => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    private IEnumerable<string> Common()
    {
        yield return "-i"; yield return _keyFile;
        foreach (var o in new[] { "BatchMode=yes", "StrictHostKeyChecking=no", $"UserKnownHostsFile={NullDevice}", "LogLevel=ERROR", "ConnectTimeout=10", "ServerAliveInterval=10", "ServerAliveCountMax=3" })
        { yield return "-o"; yield return o; }
    }

    private ProcessStartInfo Ssh(string command)
    {
        var psi = new ProcessStartInfo("ssh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in Common()) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add($"{_user}@{Address}");
        psi.ArgumentList.Add(command);
        return psi;
    }

    public override async Task<string> RunAsync(string shellCommand, TimeSpan timeout, CancellationToken ct)
    {
        using var process = new Process { StartInfo = Ssh(shellCommand) };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"'{Name}': the command took more than {timeout.TotalSeconds:0} s.");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException($"'{Name}': the command failed ({process.ExitCode}): {output.ToString().Trim()}");
        return output.ToString();
    }

    private static string Short(string text)
    {
        var line = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return line.Length <= 200 ? line : line[..200] + "...";
    }

    public override async Task WaitReadyAsync(CancellationToken ct, Action<string> log)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        string last = "no answer yet";
        int tries = 0;
        while (true)
        {
            try { await RunAsync("true", TimeSpan.FromSeconds(20), ct); break; }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                last = Short(ex.Message);
                tries++;
                if (waited.Elapsed > TimeSpan.FromMinutes(4))
                    throw new TimeoutException($"'{Name}' ({Address}) did not accept SSH within 4 minutes ({tries} tries). Last answer: {last}");
                if (tries % 6 == 0) log($"  still waiting for SSH on {Name} ({Address}) after {waited.Elapsed.TotalSeconds:0} s. Last answer: {last}");
                await Task.Delay(5000, ct);
            }
        }
        log($"  {Name}: SSH answers after {waited.Elapsed.TotalSeconds:0} s");

        // The machine sets itself up on first start (traffic shaping tools, and for the TURN machine the server).
        try { await RunAsync("cloud-init status --wait || true", TimeSpan.FromMinutes(5), ct); }
        catch (TimeoutException) { throw new TimeoutException($"'{Name}' ({Address}) was still setting itself up after 5 more minutes."); }
        log($"  {Name}: set up after {waited.Elapsed.TotalSeconds:0} s");
    }

    public override async Task DeployAsync(string archive, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("scp") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in Common()) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add(archive);
        psi.ArgumentList.Add($"{_user}@{Address}:nettest.tar.gz");
        using (var scp = Process.Start(psi)!)
        {
            await scp.WaitForExitAsync(ct);
            if (scp.ExitCode != 0) throw new InvalidOperationException($"'{Name}': copying the agent failed: {await scp.StandardError.ReadToEndAsync(ct)}");
        }
        await RunAsync("rm -rf nt && mkdir -p nt && tar -xzf nettest.tar.gz -C nt && chmod +x nt/Yaparr.NetAgent", TimeSpan.FromMinutes(2), ct);
    }

    public override RunningAgent StartAgent(string[] agentArgs, bool behindNat, Action<string> onLine)
    {
        var command = "cd nt && exec " + (behindNat ? "ip netns exec behind " : "") + "./Yaparr.NetAgent " + string.Join(' ', agentArgs);
        return new RunningAgent(new Process { StartInfo = Ssh(command) }, onLine);
    }
}
