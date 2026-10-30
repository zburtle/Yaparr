using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
/// First-time setup: makes an SSH key pair for the tool, adds the public half to your DigitalOcean account, and writes
/// nettest.json. Run it yourself with your token in the environment; nothing is stored.
/// </summary>
internal static class Setup
{
    public static string DefaultKeyFile() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "yaparr-nettest");

    /// <summary>A name DigitalOcean accepts for the key, from this computer's name.</summary>
    public static string KeyName(string machine)
    {
        var clean = Regex.Replace(machine.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return "yaparr-nettest-" + (clean.Length == 0 ? "pc" : clean[..Math.Min(clean.Length, 40)]);
    }

    /// <summary>Puts the two key settings into existing settings text (or makes new settings), keeping everything else.</summary>
    public static string MergeSettings(string? existing, string keyFile, string keyName)
    {
        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(existing)
                ? new JsonObject()
                : JsonNode.Parse(existing, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("nettest.json exists but cannot be read; fix or delete it and run setup again.");
        }
        root["sshKeyFile"] = keyFile;
        root["sshKeyName"] = keyName;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static async Task<int> RunAsync(string? keyFileOption, string? nameOption, string settingsPath, Settings settings, CancellationToken ct)
    {
        using var api = new DigitalOcean(settings.Token);
        var (limit, email) = await api.AccountAsync(ct);
        Console.WriteLine($"The token works for {email} (droplet limit {limit}).");

        var keyFile = keyFileOption ?? DefaultKeyFile();
        string publicKey = await EnsureKeyAsync(keyFile, Console.WriteLine, ct);

        var keys = await api.ListKeysAsync(ct);
        var same = keys.FirstOrDefault(k => SameKey(k.PublicKey, publicKey));
        string name;
        if (same != null)
        {
            name = same.Name;
            Console.WriteLine($"That key is already in your account as '{name}'; nothing to upload.");
        }
        else
        {
            name = nameOption ?? KeyName(Environment.MachineName);
            if (keys.Any(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)))
                name += "-" + DateTime.Now.ToString("yyMMddHHmm");
            var added = await api.AddKeyAsync(name, publicKey, ct);
            Console.WriteLine($"Added the public key to your DigitalOcean account as '{added.Name}' (fingerprint {added.Fingerprint}).");
            name = added.Name;
        }

        string? old = File.Exists(settingsPath) ? await File.ReadAllTextAsync(settingsPath, ct) : null;
        if (old != null) File.Copy(settingsPath, settingsPath + ".bak", overwrite: true);
        await File.WriteAllTextAsync(settingsPath, MergeSettings(old, keyFile, name), ct);
        Console.WriteLine($"{(old != null ? "Updated" : "Wrote")} {settingsPath}{(old != null ? $" (the old one is kept as {settingsPath}.bak)" : "")}.");
        Console.WriteLine();
        Console.WriteLine("Next:  Yaparr.NetTest check            (confirms everything, creates nothing)");
        Console.WriteLine("       Yaparr.NetTest run basic --dry-run");
        return 0;
    }

    /// <summary>Makes the key pair if there is none (never overwriting one), and returns the public key text.</summary>
    public static async Task<string> EnsureKeyAsync(string keyFile, Action<string> say, CancellationToken ct)
    {
        var publicFile = keyFile + ".pub";
        if (!File.Exists(keyFile))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyFile))!);
            say($"Creating a new SSH key at {keyFile} (no passphrase, because the tool uses it unattended)...");
            await Run("ssh-keygen", new[] { "-t", "ed25519", "-N", "", "-C", "yaparr-nettest", "-f", keyFile, "-q" }, ct);
        }
        else say($"Using the SSH key that already exists at {keyFile}.");

        // The public half: from its file, or worked out from the private one.
        string publicKey = File.Exists(publicFile)
            ? (await File.ReadAllTextAsync(publicFile, ct)).Trim()
            : (await Run("ssh-keygen", new[] { "-y", "-f", keyFile }, ct)).Trim();
        if (!publicKey.StartsWith("ssh-")) throw new InvalidOperationException("That does not look like a public SSH key: " + publicKey[..Math.Min(40, publicKey.Length)]);
        return publicKey;
    }

    // Two public keys are the same if their type and key data match; the comment at the end does not count.
    public static bool SameKey(string a, string b)
    {
        static string[] Parts(string k) => k.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var x = Parts(a); var y = Parts(b);
        return x.Length >= 2 && y.Length >= 2 && x[0] == y[0] && x[1] == y[1];
    }

    private static async Task<string> Run(string program, string[] arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        Process process;
        try { process = Process.Start(psi)!; }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException($"Could not run {program}. The OpenSSH client must be installed (it is part of Windows 10 and 11)."); }
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0) throw new InvalidOperationException($"{program} failed: {(await error).Trim()}");
            return await output;
        }
    }
}
