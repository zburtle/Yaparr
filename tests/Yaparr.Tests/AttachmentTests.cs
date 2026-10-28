using System.Buffers.Binary;
using System.Net;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Transport;

namespace Yaparr.Tests;

public class AttachmentTests
{
    private static byte[] Png(int w, int h)
    {
        var b = new byte[40];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), (uint)h);
        return b;
    }

    // A GIF whose single frame carries about `payload` bytes of image data. Not a real picture, but a valid file structure.
    private static byte[] Gif(int width, int height, int payload, int frames = 1)
    {
        using var ms = new MemoryStream();
        ms.Write("GIF89a"u8);
        ms.Write(new byte[] { (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0, 0, 0 });
        for (int f = 0; f < frames; f++)
        {
            ms.WriteByte(0x2C);
            ms.Write(new byte[] { 0, 0, 0, 0, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0 });
            ms.WriteByte(2); // LZW minimum code size
            var rng = new Random(f + 1);
            for (int left = payload; left > 0;)
            {
                int n = Math.Min(255, left);
                var block = new byte[n];
                rng.NextBytes(block);
                ms.WriteByte((byte)n);
                ms.Write(block);
                left -= n;
            }
            ms.WriteByte(0);
        }
        ms.WriteByte(0x3B);
        return ms.ToArray();
    }

    private static byte[] Jpeg(int w, int h)
    {
        var b = new List<byte> { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 3 };
        b.AddRange(new byte[9]);
        return b.ToArray();
    }

    [Fact]
    public void Picture_headers_are_read_without_decoding_and_bad_ones_are_refused()
    {
        Assert.True(ImageSniffer.TryRead(Png(640, 480), out var png));
        Assert.Equal(("image/png", 640, 480), (png.Mime, png.Width, png.Height));

        Assert.True(ImageSniffer.TryRead(Jpeg(1024, 768), out var jpeg));
        Assert.Equal(("image/jpeg", 1024, 768), (jpeg.Mime, jpeg.Width, jpeg.Height));

        Assert.True(ImageSniffer.TryRead(Gif(100, 50, 600, frames: 3), out var gif));
        Assert.Equal(("image/gif", 100, 50, 3), (gif.Mime, gif.Width, gif.Height, gif.Frames));

        Assert.False(ImageSniffer.TryRead(new byte[100], out _));
        Assert.False(ImageSniffer.TryRead("MZ not a picture"u8, out _));
        Assert.False(ImageSniffer.TryRead(Png(60000, 60000), out _));          // decompression bomb
        Assert.False(ImageSniffer.TryRead(Png(0, 10), out _));
        Assert.False(ImageSniffer.TryRead(Gif(100, 100, 10, frames: 700), out _)); // too many frames
        Assert.False(ImageSniffer.TryRead(Gif(5000, 5000, 10), out _));           // GIFs are decoded frame by frame, so keep them small
        var cut = Gif(10, 10, 600);
        Assert.False(ImageSniffer.TryRead(cut.AsSpan(0, cut.Length - 40), out _)); // truncated
    }

    [Fact]
    public void An_attachment_survives_a_trip_through_message_text_and_odd_text_is_not_mistaken_for_one()
    {
        var thumb = Jpeg(48, 32);
        var att = new Attachment(Attachment.HashOf("x"u8), "cat.gif", 1234, "image/gif", 300, 200, thumb);

        Assert.True(Attachment.TryParse(att.ToText("look at this"), out var parsed, out var caption));
        Assert.Equal("look at this", caption);
        Assert.Equal(att.Hash, parsed.Hash);
        Assert.Equal((300, 200, 1234L), (parsed.Width, parsed.Height, parsed.Size));
        Assert.Equal(thumb, parsed.Thumbnail);

        Assert.False(Attachment.TryParse("just a normal message", out _, out var plain));
        Assert.Equal("just a normal message", plain);

        // Anything unreasonable in the description makes it an ordinary message.
        Assert.False(Attachment.TryParse(att.ToText("").Replace("image/gif", "application/x-evil"), out _, out _));
        Assert.False(Attachment.TryParse(att.ToText("").Replace(att.Hash, "zz" + att.Hash[2..]), out _, out _));
        Assert.False(Attachment.TryParse(att.ToText("").Replace("\"w\":300", "\"w\":300000"), out _, out _));

        // A thumbnail that is too big or not a small picture is dropped, but the attachment is still shown.
        var fat = att with { Thumbnail = new byte[4000] };
        Assert.True(Attachment.TryParse(fat.ToText(""), out var noThumb, out _));
        Assert.Null(noThumb.Thumbnail);
    }

    private static ChatNode Node(InMemoryDiscoveryHub hub, string name, long maxAuto = Attachment.MaxBytes) => new(new ChatNodeOptions
    {
        DisplayName = name,
        EnableRelaying = true,
        RelayFallbackDelay = TimeSpan.FromMilliseconds(500),
        DiscoveryFactory = mux => hub.CreateClient(new IPEndPoint(IPAddress.Loopback, mux.LocalPort)),
        DiscoveryInterval = TimeSpan.FromMilliseconds(300),
        Network = TestUtil.FastNetwork,
        MaxAutoFetchBytes = maxAuto,
    });

    [Fact]
    public async Task A_picture_sent_to_a_room_arrives_intact_on_its_own_channel_while_chat_keeps_working()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = Node(hub, "a");
        using var b = Node(hub, "b");
        var ra = await a.JoinRoomAsync("Pics");
        var rb = await b.JoinRoomAsync("Pics");
        await TestUtil.WaitFor(() => ra.MemberStates.Count == 1 && rb.MemberStates.Count == 1);

        var file = Gif(320, 240, 2_500_000);
        var att = a.PrepareAttachment(file, "big.gif");
        var got = new TaskCompletionSource<ChatMessage>();
        rb.MessageReceived += m => { if (m.Attachment != null) got.TrySetResult(m); };

        await ra.SendAsync(att.ToText("a gif"));
        var text = new TaskCompletionSource<bool>();
        rb.MessageReceived += m => { if (m.Text == "still chatting") text.TrySetResult(true); };
        await ra.SendAsync("still chatting");

        var message = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("a gif", message.Text);
        Assert.Equal(att.Hash, message.Attachment!.Hash);
        await text.Task.WaitAsync(TimeSpan.FromSeconds(10)); // a big transfer does not hold up ordinary messages

        await TestUtil.WaitFor(() => b.HasAttachment(att.Hash), timeoutMs: 30_000);
        Assert.Equal(file, b.ReadAttachment(att.Hash));
    }

    [Fact]
    public async Task A_big_file_waits_for_a_click_and_a_file_nobody_has_is_reported_unavailable()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = Node(hub, "a");
        using var b = Node(hub, "b", maxAuto: 10_000);
        var ra = await a.JoinRoomAsync("Limits");
        var rb = await b.JoinRoomAsync("Limits");
        await TestUtil.WaitFor(() => ra.MemberStates.Count == 1 && rb.MemberStates.Count == 1);

        var att = a.PrepareAttachment(Gif(64, 64, 50_000), "med.gif");
        var got = new TaskCompletionSource<ChatMessage>();
        rb.MessageReceived += m => { if (m.Attachment != null) got.TrySetResult(m); };
        await ra.SendAsync(att.ToText(""));
        var message = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await Task.Delay(500);
        Assert.False(b.HasAttachment(att.Hash)); // above the automatic limit
        Assert.Equal(AttachmentState.NeedsConfirmation, await b.FetchAttachmentAsync(message));
        Assert.Equal(AttachmentState.Ready, await b.FetchAttachmentAsync(message, force: true));
        Assert.True(b.HasAttachment(att.Hash));

        var ghost = new Attachment(Attachment.HashOf("nobody has this"u8), "x.gif", 5000, "image/gif", 10, 10);
        var missing = new ChatMessage(message.From, "Limits", "ghost", "", DateTimeOffset.UtcNow, false, ghost);
        Assert.Equal(AttachmentState.Unavailable, await b.FetchAttachmentAsync(missing, force: true));
    }

    [Fact]
    public void Only_pictures_of_a_sensible_size_can_be_prepared_for_sending()
    {
        using var node = new ChatNode(new ChatNodeOptions { Traversal = new TraversalOptions { Enabled = false } });
        Assert.Throws<ArgumentException>(() => node.PrepareAttachment("not a picture"u8.ToArray(), "x.txt"));
        Assert.Throws<ArgumentException>(() => node.PrepareAttachment(Array.Empty<byte>(), "x.png"));
        var ok = node.PrepareAttachment(Png(10, 10), "../../evil\\name.png");
        Assert.Equal("evilname.png", ok.FileName);
        Assert.True(node.HasAttachment(ok.Hash));
    }
}
