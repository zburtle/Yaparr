// Runs Yaparr across real machines and checks what happens.
//
//   Yaparr.NetTest run <scenario.json> [--local] [--dry-run] [--keep] [--settings nettest.json]
//   Yaparr.NetTest setup             First time: makes an SSH key, adds it to your DigitalOcean account, writes nettest.json.
//   Yaparr.NetTest bake              Makes a snapshot with the tools installed so machines start ready (--regions a,b to limit, --delete to remove).
//   Yaparr.NetTest check             Verifies your DigitalOcean token and SSH key.
//   Yaparr.NetTest cleanup           Deletes every machine the tool created (tag yaparr-nettest).
//   Yaparr.NetTest list              Lists the scenarios that come with the tool.

var command = args.FirstOrDefault()?.ToLowerInvariant();
var rest = args.Skip(1).ToList();
bool Flag(string name) => rest.Remove(name);
string? Option(string name)
{
    int i = rest.IndexOf(name);
    if (i < 0 || i + 1 >= rest.Count) return null;
    var value = rest[i + 1];
    rest.RemoveRange(i, 2);
    return value;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; Console.WriteLine("Stopping and cleaning up..."); cts.Cancel(); };

try
{
    switch (command)
    {
        case "run":
            {
                var settingsPath = Option("--settings");
                var local = Flag("--local");
                var dry = Flag("--dry-run");
                var keep = Flag("--keep");
                var path = rest.FirstOrDefault() ?? throw new InvalidOperationException("Name a scenario file.");
                if (!File.Exists(path) && File.Exists(Path.Combine(AppContext.BaseDirectory, "scenarios", path + ".json"))) path = Path.Combine(AppContext.BaseDirectory, "scenarios", path + ".json");
                var scenario = Scenario.Load(path);
                var settings = Settings.Load(settingsPath);
                if (!local && !dry && settings.SshKeyName.Length == 0) throw new InvalidOperationException("nettest.json needs sshKeyName and sshKeyFile (see nettest.example.json).");
                var runner = new Runner(scenario, settings, new RunOptions { Local = local, DryRun = dry, Keep = keep });
                return await runner.RunAsync(cts.Token);
            }

        case "setup":
            {
                var settingsPath = Option("--settings") ?? "nettest.json";
                var keyFile = Option("--key");
                var keyName = Option("--name");
                return await Setup.RunAsync(keyFile, keyName, settingsPath, Settings.Load(File.Exists(settingsPath) ? settingsPath : null), cts.Token);
            }

        case "check":
            {
                var settings = Settings.Load(Option("--settings"));
                using var api = new DigitalOcean(settings.Token);
                var (limit, email) = await api.AccountAsync(cts.Token);
                Console.WriteLine($"Token works for {email}; droplet limit {limit}.");
                long id = await api.FindKeyAsync(settings.SshKeyName, cts.Token);
                Console.WriteLine($"SSH key '{settings.SshKeyName}' found (id {id}).");
                Console.WriteLine(File.Exists(settings.SshKeyFile) ? $"Private key file found: {settings.SshKeyFile}" : $"PRIVATE KEY FILE MISSING: {settings.SshKeyFile}");
                var existing = await api.ListByTagAsync(settings.Tag, cts.Token);
                Console.WriteLine($"{existing.Count} machine(s) tagged '{settings.Tag}' exist now.");
                return 0;
            }

        case "bake":
            {
                var settings = Settings.Load(Option("--settings"));
                var delete = Flag("--delete");
                var regions = Option("--regions")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return await Bake.RunAsync(settings, regions, delete, cts.Token);
            }

        case "cleanup":
            {
                var settings = Settings.Load(Option("--settings"));
                using var api = new DigitalOcean(settings.Token);
                var existing = await api.ListByTagAsync(settings.Tag, cts.Token);
                foreach (var d in existing) Console.WriteLine($"  deleting {d.Name} ({d.Ip}, {d.Region})");
                if (existing.Count > 0) await api.DeleteByTagAsync(settings.Tag, cts.Token);
                Console.WriteLine($"Deleted {existing.Count} machine(s).");
                return 0;
            }

        case "list":
            {
                foreach (var f in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "scenarios"), "*.json").OrderBy(f => f))
                {
                    var s = Scenario.Load(f);
                    Console.WriteLine($"  {Path.GetFileNameWithoutExtension(f),-14} {s.Nodes.Count} nodes, {s.DurationSeconds} s  {s.Description}");
                }
                return 0;
            }

        default:
            Console.WriteLine("usage: Yaparr.NetTest setup | bake [--regions a,b] [--delete] | run <scenario> [--local] [--dry-run] [--keep] | check | cleanup | list");
            return 2;
    }
}
catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or System.Text.Json.JsonException or FileNotFoundException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
