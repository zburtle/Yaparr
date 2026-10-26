using System.Text;
using Yaparr.Discovery.Dht;

namespace Yaparr.Tests;

public class BencodeTests
{
    [Fact]
    public void Encodes_known_vectors()
    {
        var d = new BDict { ["cow"] = "moo", ["spam"] = new List<object> { "a", 42L } };
        Assert.Equal("d3:cow3:moo4:spaml1:ai42eee", Encoding.ASCII.GetString(Bencode.Encode(d)));
    }

    [Fact]
    public void Round_trips_binary_data()
    {
        var d = new BDict { ["id"] = new byte[] { 0, 255, 1, 2 }, ["n"] = -7L, ["nested"] = new BDict { ["x"] = 1L } };
        var back = (BDict)Bencode.Decode(Bencode.Encode(d));
        Assert.Equal(new byte[] { 0, 255, 1, 2 }, back.Bytes("id"));
        Assert.Equal(-7, back.Int("n"));
        Assert.Equal(1, back.Dict("nested")!.Int("x"));
    }

    [Theory]
    [InlineData("i12")]
    [InlineData("5:abc")]
    [InlineData("l")]
    [InlineData("d1:ae")]
    [InlineData("i1ex")]
    [InlineData("x")]
    public void Rejects_malformed_input(string text) =>
        Assert.Throws<FormatException>(() => Bencode.Decode(Encoding.ASCII.GetBytes(text)));
}
