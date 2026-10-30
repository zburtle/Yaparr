/// <summary>
/// Makes the snapshot that scenarios start from: one machine with every tool installed, saved and copied to the regions
/// scenarios use, then deleted. Machines created from it are ready as soon as they boot.
/// </summary>
internal static class Bake
{
    public static async Task<int> RunAsync(Settings settings, IReadOnlyList<string>? regions, bool delete, CancellationToken ct)
    {
        if (settings.SnapshotName.Length == 0) throw new InvalidOperationException("snapshotName in nettest.json is empty.");
        if (!delete && !File.Exists(settings.SshKeyFile)) throw new InvalidOperationException($"The SSH key file '{settings.SshKeyFile}' (sshKeyFile in nettest.json) does not exist.");
        using var api = new DigitalOcean(settings.Token);

        var old = await api.FindSnapshotAsync(settings.SnapshotName, ct);
        if (delete)
        {
            if (old == null) Console.WriteLine($"No snapshot named '{settings.SnapshotName}'.");
            else { await api.DeleteImageAsync(old.Id, ct); Console.WriteLine($"Deleted the snapshot '{old.Name}'."); }
            return 0;
        }

        regions ??= ScenarioRegions();
        if (regions.Count == 0) throw new InvalidOperationException("No regions to bake for.");
        var source = regions[0];
        Console.WriteLine($"Baking '{settings.SnapshotName}' in {source}, then copying to: {string.Join(", ", regions.Skip(1))}");

        // The old snapshot is kept until the new one is complete; the new one has a different name until then.
        var newName = old == null ? settings.SnapshotName : settings.SnapshotName + "-new";
        if (old != null) Console.WriteLine($"A snapshot named '{old.Name}' exists already ({string.Join(", ", old.Regions)}); it is replaced when the new one is ready.");

        long keyId = await api.FindKeyAsync(settings.SshKeyName, ct);
        var tags = new[] { settings.Tag, "yaparr-bake" };
        var droplet = await api.CreateAsync("yaparr-nt-bake", source, settings.Size, settings.Image, keyId, tags, Scripts.BakeInit(), ct);
        try
        {
            droplet = await api.WaitActiveAsync(droplet.Id, ct);
            Console.WriteLine($"Machine {droplet.Ip} is running; installing tools...");
            var machine = new SshMachine("bake", droplet.Ip!, settings.SshUser, settings.SshKeyFile);
            await machine.WaitReadyAsync(ct, Console.WriteLine);
            Console.WriteLine("Checking the tools:\n" + (await machine.RunAsync(Scripts.BakeCheck, TimeSpan.FromMinutes(2), ct)).Trim());
            // Make the next start behave like a first start, then save the machine switched off.
            await machine.RunAsync("cloud-init clean --logs; sync", TimeSpan.FromMinutes(1), ct);

            Console.WriteLine("Shutting down and taking the snapshot (a few minutes)...");
            await api.ShutdownAsync(droplet.Id, ct);
            await api.SnapshotAsync(droplet.Id, newName, ct);
            var made = await api.FindSnapshotAsync(newName, ct) ?? throw new InvalidOperationException("The snapshot was made but is not in the list.");

            var others = regions.Skip(1).Where(r => !made.Regions.Contains(r)).Distinct().ToList();
            if (others.Count > 0)
            {
                Console.WriteLine($"Copying to {others.Count} region(s) (a few minutes each, in parallel)...");
                await Task.WhenAll(others.Select(async r =>
                {
                    await api.TransferAsync(made.Id, r, ct);
                    Console.WriteLine($"  copied to {r}");
                }));
            }

            if (old != null)
            {
                await api.DeleteImageAsync(old.Id, ct);
                Console.WriteLine($"The older snapshot is deleted. The new one is named '{newName}'; run finds it under that name.");
            }
            Console.WriteLine($"Done. Snapshot '{newName}' is in {source}{(others.Count > 0 ? ", " + string.Join(", ", others) : "")}.");
            return 0;
        }
        finally
        {
            try { await api.DeleteDropletAsync(droplet.Id, CancellationToken.None); Console.WriteLine("Deleted the baking machine."); }
            catch (Exception ex) { Console.WriteLine($"Could not delete the baking machine {droplet.Id}: {ex.Message} (run 'cleanup')."); }
        }
    }

    /// <summary>Every region the bundled scenarios use.</summary>
    public static List<string> ScenarioRegions()
    {
        var regions = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "scenarios"), "*.json"))
            foreach (var n in Scenario.Load(f).Nodes) regions.Add(n.Region);
        return regions.ToList();
    }
}
