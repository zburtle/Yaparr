using System.Text.Json;

namespace Yaparr.Tests;

// The first-time setup of the network test tool, apart from the calls to DigitalOcean.
public class NetTestSetupTests
{
    [Theory]
    [InlineData("DESKTOP-ABC123", "yaparr-nettest-desktop-abc123")]
    [InlineData("Zach's  PC!!", "yaparr-nettest-zach-s-pc")]
    [InlineData("###", "yaparr-nettest-pc")]
    public void The_key_name_is_made_from_the_computer_name_in_a_form_DigitalOcean_accepts(string machine, string expected) =>
        Assert.Equal(expected, Setup.KeyName(machine));

    [Fact]
    public void A_very_long_computer_name_gives_a_short_enough_key_name() =>
        Assert.True(Setup.KeyName(new string('x', 200)).Length <= 60);

    [Fact]
    public void Settings_are_made_new_or_updated_without_losing_what_else_is_in_them()
    {
        var fresh = JsonDocument.Parse(Setup.MergeSettings(null, "C:/keys/k", "my-key")).RootElement;
        Assert.Equal("C:/keys/k", fresh.GetProperty("sshKeyFile").GetString());
        Assert.Equal("my-key", fresh.GetProperty("sshKeyName").GetString());

        var merged = JsonDocument.Parse(Setup.MergeSettings("""
            { // my settings
              "size": "s-2vcpu-2gb", "maxDroplets": 4, "sshKeyName": "old",
            }
            """, "C:/keys/new", "new-key")).RootElement;
        Assert.Equal("s-2vcpu-2gb", merged.GetProperty("size").GetString());
        Assert.Equal(4, merged.GetProperty("maxDroplets").GetInt32());
        Assert.Equal("new-key", merged.GetProperty("sshKeyName").GetString());
        Assert.Equal("C:/keys/new", merged.GetProperty("sshKeyFile").GetString());
    }

    [Fact]
    public void Settings_that_cannot_be_read_are_not_overwritten_blindly() =>
        Assert.Throws<InvalidOperationException>(() => Setup.MergeSettings("{ not json", "k", "n"));

    [Fact]
    public void The_same_key_is_recognised_whatever_the_comment_says()
    {
        Assert.True(Setup.SameKey("ssh-ed25519 AAAAC3Nza me@laptop", "ssh-ed25519 AAAAC3Nza yaparr-nettest"));
        Assert.False(Setup.SameKey("ssh-ed25519 AAAAC3Nza me", "ssh-ed25519 BBBBC3Nza me"));
        Assert.False(Setup.SameKey("ssh-rsa AAAAC3Nza me", "ssh-ed25519 AAAAC3Nza me"));
        Assert.False(Setup.SameKey("", "ssh-ed25519 AAAAC3Nza me"));
    }

    [Fact]
    public async Task A_key_is_made_unattended_and_an_existing_one_is_never_overwritten()
    {
        if (!OperatingSystem.IsWindows() && !File.Exists("/usr/bin/ssh-keygen")) return;   // nothing to test with
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-setup-" + Guid.NewGuid().ToString("N"));
        var keyFile = Path.Combine(dir, "sub", "k");
        try
        {
            var said = new List<string>();
            var first = await Setup.EnsureKeyAsync(keyFile, said.Add, CancellationToken.None);
            Assert.StartsWith("ssh-ed25519 ", first);
            Assert.True(File.Exists(keyFile));
            var privateBytes = await File.ReadAllBytesAsync(keyFile);

            var again = await Setup.EnsureKeyAsync(keyFile, said.Add, CancellationToken.None);
            Assert.Equal(first, again);
            Assert.Equal(privateBytes, await File.ReadAllBytesAsync(keyFile));
            Assert.Contains(said, s => s.StartsWith("Creating"));
            Assert.Contains(said, s => s.StartsWith("Using the SSH key that already exists"));

            // Without its public file, the public key is worked out from the private one.
            File.Delete(keyFile + ".pub");
            Assert.True(Setup.SameKey(first, await Setup.EnsureKeyAsync(keyFile, said.Add, CancellationToken.None)));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }
}
