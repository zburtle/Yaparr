using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class LinkFileTests
{
    [Fact]
    public void A_link_file_carries_its_own_passphrase_so_the_new_device_types_nothing()
    {
        using var first = PeerIdentity.Generate();
        var entries = new[] { new AccountEntry("room:abc", "{\"name\":\"Fellas\"}", 5) };
        var passphrase = DeviceLink.GeneratePassphrase();

        // What goes into the file: the code and its passphrase together (the same text a QR code holds), with the rooms.
        var text = LinkQr.Payload(DeviceLink.Create(first, "Zed", entries, passphrase), passphrase) + Environment.NewLine;

        // What the new device does with the file's text: take both parts out and open it.
        Assert.True(LinkQr.TryParse(text, out var code, out var carried));
        var linked = DeviceLink.Open(code, carried);
        Assert.Equal("Zed", linked.DisplayName);
        Assert.Single(linked.Entries);
        Assert.Equal("room:abc", linked.Entries[0].Key);
        Assert.Equal(first.AccountId, linked.Identity.AccountId);
        linked.Identity.Dispose();
    }

    [Fact]
    public void An_older_code_without_a_passphrase_is_not_taken_for_a_file_that_carries_one()
    {
        using var first = PeerIdentity.Generate();
        var code = DeviceLink.Create(first, "Zed", Array.Empty<AccountEntry>(), DeviceLink.GeneratePassphrase());
        Assert.False(LinkQr.TryParse(code, out _, out _));       // the new device still asks for the passphrase
    }
}
