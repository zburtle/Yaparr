using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class LinkQrTests
{
    private static string MakeCode(out string passphrase)
    {
        using var identity = PeerIdentity.Generate();
        passphrase = DeviceLink.GeneratePassphrase();
        // A code for a QR leaves the rooms out.
        return DeviceLink.Create(identity, "Zach", Array.Empty<AccountEntry>(), passphrase);
    }

    [Fact]
    public void A_payload_gives_back_the_code_and_the_passphrase()
    {
        var code = MakeCode(out var passphrase);
        Assert.True(LinkQr.TryParse(LinkQr.Payload(code, passphrase), out var gotCode, out var gotPassphrase));
        Assert.Equal(code, gotCode);
        Assert.Equal(passphrase, gotPassphrase);
    }

    [Fact]
    public void What_a_scan_returns_opens_the_profile()
    {
        var code = MakeCode(out var passphrase);
        Assert.True(LinkQr.TryParse("  " + LinkQr.Payload(code, passphrase) + "\n", out var gotCode, out var gotPassphrase));
        using var linked = DeviceLink.Open(gotCode, gotPassphrase).Identity;
        Assert.NotNull(linked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/")]
    [InlineData("YLNK1.abc")]
    [InlineData("YLNK1.|K7QD-M3XF")]
    [InlineData("YLNK1.abc|")]
    [InlineData("abc|K7QD-M3XF-9PLR-2WTE-HV8N")]
    public void Anything_else_is_not_a_link(string? text) =>
        Assert.False(LinkQr.TryParse(text, out _, out _));

    [Fact]
    public void A_code_without_rooms_fits_in_a_QR_code()
    {
        var code = MakeCode(out var passphrase);
        var matrix = LinkQr.Matrix(LinkQr.Payload(code, passphrase));
        Assert.Equal(matrix.GetLength(0), matrix.GetLength(1));
        Assert.InRange(matrix.GetLength(0), 21, 177);
        bool anyDark = false, anyLight = false;
        foreach (var square in matrix) { anyDark |= square; anyLight |= !square; }
        Assert.True(anyDark && anyLight);
    }

    [Fact]
    public void Too_much_text_is_refused_with_an_argument_exception() =>
        Assert.Throws<ArgumentException>(() => LinkQr.Matrix(new string('x', 5000)));

    [Fact]
    public void Only_a_link_code_makes_a_payload() =>
        Assert.Throws<ArgumentException>(() => LinkQr.Payload("hello", "K7QD"));
}
