using Yaparr.Chat;
using Yaparr.Transport;

namespace Yaparr.Tests;

public class UserRelayTests
{
    private static readonly TurnOptions Own = new() { Server = "mine.example.com:3478", Username = "a", Credential = "b", Enabled = true };

    private static ChatNode Node(TurnOptions? own) => new(new ChatNodeOptions
    {
        Traversal = new TraversalOptions { Turn = own, Enabled = false },
    });

    [Fact]
    public void The_relay_the_user_set_up_is_used_when_it_is_on()
    {
        using var node = Node(Own);
        Assert.Equal("mine.example.com:3478", node.EffectiveTurn()!.Server);
    }

    [Fact]
    public void There_is_no_relay_when_none_is_set_up_or_it_is_switched_off()
    {
        using (var node = Node(null)) Assert.Null(node.EffectiveTurn());
        using (var node = Node(new TurnOptions { Server = Own.Server, Enabled = false })) Assert.Null(node.EffectiveTurn());
    }

    [Fact]
    public void A_shared_relay_reads_back_the_same()
    {
        var code = RelayShare.Create(Own);
        Assert.StartsWith(RelayShare.Prefix, code);
        Assert.True(RelayShare.TryParse(code, out var turn));
        Assert.Equal(Own.Server, turn!.Server);
        Assert.Equal("a", turn.Username);
        Assert.Equal("b", turn.Credential);
        Assert.True(turn.Enabled);
        Assert.False(turn.Always);
    }

    [Fact]
    public void A_code_survives_odd_characters_in_the_password_and_being_wrapped_or_padded_with_spaces()
    {
        var hard = new TurnOptions { Server = "turn.example.com:443", Username = "yaparr", Credential = "p\"w\\ ü/+=é" };
        var code = RelayShare.Create(hard);
        Assert.DoesNotContain('=', code);
        var wrapped = "  " + string.Join("\r\n", Enumerable.Range(0, code.Length / 20 + 1).Select(i => code.Substring(i * 20, Math.Min(20, code.Length - i * 20)))) + "  \n";
        Assert.True(RelayShare.TryParse(wrapped, out var turn));
        Assert.Equal(hard.Credential, turn!.Credential);
        Assert.Equal(hard.Server, turn.Server);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("yaparr-relay:")]
    [InlineData("yaparr-relay:!!!!")]
    [InlineData("yaparr-relay:e30")]
    public void Text_that_is_not_a_relay_code_is_refused(string? text)
    {
        Assert.False(RelayShare.TryParse(text, out var turn));
        Assert.Null(turn);
    }

    [Fact]
    public void A_code_with_a_bad_server_or_a_control_character_is_refused()
    {
        string Make(string json) => RelayShare.Prefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.True(RelayShare.TryParse(Make("{\"v\":1,\"server\":\"a.example.com:3478\",\"username\":\"u\",\"credential\":\"c\"}"), out _));
        Assert.False(RelayShare.TryParse(Make("{\"v\":2,\"server\":\"a.example.com:3478\"}"), out _));
        Assert.False(RelayShare.TryParse(Make("{\"v\":1,\"server\":\"\"}"), out _));
        Assert.False(RelayShare.TryParse(Make("{\"v\":1,\"server\":\"a.example.com:99999\"}"), out _));
        Assert.False(RelayShare.TryParse(Make("{\"v\":1,\"server\":\"a.example.com:3478\",\"credential\":\"x\\ny\"}"), out _));
        Assert.False(RelayShare.TryParse(Make("{\"v\":1,\"server\":\"" + new string('a', 300) + ":3478\"}"), out _));
    }

    [Fact]
    public void A_relay_with_no_server_cannot_be_shared()
    {
        Assert.Throws<ArgumentException>(() => RelayShare.Create(new TurnOptions { Server = " " }));
    }

    [Fact]
    public void Network_settings_still_keep_the_relay_and_ignore_the_old_built_in_relay_choice()
    {
        var path = Path.Combine(Path.GetTempPath(), "yaparr-net-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{ \"defaultRelay\": false }");
            TraversalOptions.SaveTurn(path, Own);
            var loaded = TraversalOptions.Load(path);
            Assert.Equal(Own.Server, loaded.Turn!.Server);
            Assert.Equal("b", loaded.Turn.Credential);
        }
        finally { File.Delete(path); }
    }
}
