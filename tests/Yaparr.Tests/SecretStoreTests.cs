using System.Security.Cryptography;
using System.Text;
using Yaparr.Chat;
using Yaparr.Identity;
using Yaparr.Transport;

namespace Yaparr.Tests;

// Keys on disk are sealed to the Windows user; elsewhere the files stay readable but private to their owner.
public class SecretStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-secret-" + Guid.NewGuid().ToString("N"));

    public SecretStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    [Fact]
    public void A_written_file_reads_back_and_on_windows_does_not_hold_the_secret_in_the_clear()
    {
        var path = Path.Combine(_dir, "key");
        var secret = Encoding.UTF8.GetBytes("a very secret room key");
        SecretStore.WriteAllBytes(path, secret);

        Assert.Equal(secret, SecretStore.ReadAllBytes(path));
        var raw = File.ReadAllBytes(path);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(SecretStore.IsSealed(raw));
            Assert.DoesNotContain("secret room", Encoding.UTF8.GetString(raw));
        }
        else Assert.Equal(secret, raw);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void A_plain_file_from_an_earlier_version_is_read_and_then_upgraded()
    {
        var path = Path.Combine(_dir, "old");
        File.WriteAllText(path, "[Interface]\nPrivateKey = abc");

        Assert.Equal("[Interface]\nPrivateKey = abc", SecretStore.ReadAllText(path));
        Assert.Equal(OperatingSystem.IsWindows(), SecretStore.Upgrade(path));
        Assert.Equal("[Interface]\nPrivateKey = abc", SecretStore.ReadAllText(path));
        Assert.False(SecretStore.Upgrade(path));
        Assert.Equal(OperatingSystem.IsWindows(), SecretStore.IsSealed(File.ReadAllBytes(path)));
    }

    [Fact]
    public void A_sealed_file_that_cannot_be_opened_is_reported_not_returned_as_garbage()
    {
        var path = Path.Combine(_dir, "other");
        File.WriteAllBytes(path, [.. "YPSEC1"u8.ToArray(), .. new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }]);
        Assert.ThrowsAny<CryptographicException>(() => SecretStore.ReadAllBytes(path));
    }

    [Fact]
    public void Sealed_text_round_trips_and_plain_text_passes_through()
    {
        Assert.Equal("hunter2", SecretStore.OpenText(SecretStore.SealText("hunter2")));
        Assert.Equal("typed by hand", SecretStore.OpenText("typed by hand"));
        Assert.Equal("", SecretStore.OpenText(SecretStore.SealText("")));
        if (OperatingSystem.IsWindows()) Assert.DoesNotContain("hunter2", SecretStore.SealText("hunter2"));
        Assert.Equal("", SecretStore.OpenText("dpapi:not base64!"));
    }

    [Fact]
    public void The_relay_password_is_not_kept_in_the_clear_in_network_json()
    {
        var path = Path.Combine(_dir, "network.json");
        TraversalOptions.SaveTurn(path, new TurnOptions { Server = "turn.example.org:3478", Username = "user", Credential = "relay-password-123", Enabled = true });

        if (OperatingSystem.IsWindows()) Assert.DoesNotContain("relay-password-123", File.ReadAllText(path));
        Assert.Equal("relay-password-123", TraversalOptions.Load(path).Turn!.Credential);
    }

    [Fact]
    public void A_relay_password_typed_into_network_json_by_hand_still_works()
    {
        var path = Path.Combine(_dir, "network.json");
        File.WriteAllText(path, """{ "turn": { "server": "turn.example.org:3478", "username": "u", "credential": "plain", "enabled": true } }""");
        Assert.Equal("plain", TraversalOptions.Load(path).Turn!.Credential);
    }
}
