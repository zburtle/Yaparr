using System.Net;
using System.Text;
using SkiaSharp;
using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Discovery;

namespace Yaparr.Tests;

public class LinkPreviewTests
{
    private const string Page = """
        <html><head>
        <title>Plain &amp; simple</title>
        <meta property="og:title" content="The  Big&#39;s   Title">
        <meta property="og:description" content="Line one
          line two &amp; more">
        <meta property="og:image" content="/cover.png">
        <script>var x = '<meta property="og:title" content="from a script">';</script>
        </head><body><meta property="og:title" content="from the body"></body></html>
        """;

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.CornflowerBlue);
            using var paint = new SKPaint { Color = SKColors.OrangeRed };
            canvas.DrawRect(0, 0, width / 2f, height / 2f, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private sealed class Stub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _reply;
        public List<Uri> Requested { get; } = new();
        public Stub(Func<HttpRequestMessage, HttpResponseMessage> reply) => _reply = reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!);
            var response = _reply(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Image(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("image/png") } } };

    [Theory]
    [InlineData("look at https://example.com/a?b=1.", "https://example.com/a?b=1")]
    [InlineData("(https://example.com/x), nice", "https://example.com/x")]
    [InlineData("two http://one.test/ and https://two.test/", "http://one.test/")]
    [InlineData("**https://example.com/bold**", "https://example.com/bold")]
    public void The_first_web_address_is_found_as_written_without_the_punctuation_after_it(string words, string expected) =>
        Assert.Equal(expected, LinkPreview.FirstLink(words));

    [Theory]
    [InlineData("no link here")]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:secret@example.com/")]
    [InlineData("yaparr://join/abc")]
    public void Words_without_a_plain_web_address_have_no_link(string words) => Assert.Null(LinkPreview.FirstLink(words));

    [Fact]
    public void A_card_travels_with_the_message_and_the_words_stay_as_they_were()
    {
        var thumb = ImageTools.CardThumbnail(Png(300, 200))!;
        var card = new LinkPreview("https://example.com/a", "A title", "About it", thumb);
        var text = MessageEnvelope.Wrap("see https://example.com/a now", null, null, card);

        var parts = MessageEnvelope.Parse(text);
        Assert.Equal("see https://example.com/a now", parts.Body);
        Assert.Equal("https://example.com/a", parts.Preview!.Url);
        Assert.Equal("A title", parts.Preview.Title);
        Assert.Equal("About it", parts.Preview.Description);
        Assert.Equal(thumb, parts.Preview.Thumbnail);
        Assert.Equal("example.com", parts.Preview.Host);
    }

    [Fact]
    public void A_card_comes_with_a_reply_and_a_mention()
    {
        var key = new string('a', 32);
        var who = new Mention(new Yaparr.Identity.PeerId(new string('1', 64)), "alice");
        var text = MessageEnvelope.Wrap("@alice https://example.com/", key, new[] { who }, new LinkPreview("https://example.com/", "Example"));
        var parts = MessageEnvelope.Parse(text);
        Assert.Equal(key, parts.ReplyTo);
        Assert.Single(parts.Mentions);
        Assert.Equal("Example", parts.Preview!.Title);
    }

    [Fact]
    public void A_message_without_a_card_is_unchanged()
    {
        Assert.Equal("hello https://example.com/", MessageEnvelope.Wrap("hello https://example.com/", null, null, null));
        // A card with nothing to say is no card.
        Assert.Equal("hello https://example.com/", MessageEnvelope.Wrap("hello https://example.com/", null, null, new LinkPreview("https://example.com/", " ", "")));
    }

    [Fact]
    public void A_card_for_an_address_that_is_not_in_the_words_is_not_shown()
    {
        var text = MessageEnvelope.Wrap("a harmless link https://example.com/", null, null, new LinkPreview("https://evil.test/", "Totally fine"));
        // It was refused at the sending end ...
        Assert.Null(MessageEnvelope.Parse(text).Preview);
        // ... and one built by hand is refused at the receiving end.
        var forged = MessageEnvelope.Wrap("a link https://evil.test/", null, null, new LinkPreview("https://evil.test/", "Totally fine"))
            .Replace("a link https://evil.test/", "a harmless link https://example.com/");
        Assert.Null(MessageEnvelope.Parse(forged).Preview);
    }

    [Fact]
    public void A_card_is_held_to_its_limits_and_cleaned()
    {
        var card = new LinkPreview("https://example.com/", "  Tab\tand\nline " + new string('x', 400), new string('y', 900)).Normalized()!;
        Assert.True(card.Title.Length <= LinkPreview.MaxTitle);
        Assert.True(card.Description.Length <= LinkPreview.MaxDescription);
        Assert.DoesNotContain('\n', card.Title);
        Assert.DoesNotContain('\t', card.Title);
        Assert.StartsWith("Tab and line", card.Title);
        Assert.Null(new LinkPreview("javascript:alert(1)", "x").Normalized());
        Assert.Null(new LinkPreview("https://example.com/", "x", Thumbnail: new byte[LinkPreview.MaxThumbnailBytes + 1]).Normalized()!.Thumbnail);
        Assert.Null(new LinkPreview("https://example.com/", "x", Thumbnail: new byte[] { 1, 2, 3 }).Normalized()!.Thumbnail);
    }

    [Fact]
    public void A_card_that_does_not_fit_loses_its_picture_before_the_words_are_refused()
    {
        var thumb = ImageTools.CardThumbnail(Png(400, 400))!;
        var card = new LinkPreview("https://example.com/", "Title", "Description", thumb);
        // Leaves room for the header but not for the picture.
        var body = "https://example.com/ " + new string('w', MessageEnvelope.MaxText - 600);
        var parts = MessageEnvelope.Parse(MessageEnvelope.Wrap(body, null, null, card));
        Assert.Equal(body, parts.Body);
        Assert.NotNull(parts.Preview);
        Assert.Null(parts.Preview!.Thumbnail);

        // No room for a card at all: the message goes without one.
        var tight = "https://example.com/ " + new string('w', MessageEnvelope.MaxText - 22);
        Assert.Equal(tight, MessageEnvelope.Wrap(tight, null, null, card));
    }

    [Fact]
    public void The_title_description_and_picture_are_read_from_the_top_of_a_page()
    {
        var page = LinkPreviewFetcher.Read(Page, new Uri("https://example.com/articles/1"));
        Assert.Equal("The Big's Title", page.Title);
        Assert.Equal("Line one line two & more", page.Description);
        Assert.Equal(new Uri("https://example.com/cover.png"), page.Image);
    }

    [Fact]
    public void A_page_with_only_a_title_tag_and_a_description_still_has_a_card()
    {
        var page = LinkPreviewFetcher.Read("<head><title> Hi &amp; bye </title><meta name='description' content=plain></head>", new Uri("https://example.com/"));
        Assert.Equal("Hi & bye", page.Title);
        Assert.Equal("plain", page.Description);
        Assert.Null(page.Image);
    }

    [Fact]
    public void A_picture_that_is_not_a_web_address_is_ignored()
    {
        var page = LinkPreviewFetcher.Read("<meta property=\"og:title\" content=\"t\"><meta property=\"og:image\" content=\"data:image/png;base64,AAAA\">", new Uri("https://example.com/"));
        Assert.Null(page.Image);
    }

    [Fact]
    public async Task The_card_is_made_from_the_page_and_its_picture_and_the_picture_is_small_and_square()
    {
        var stub = new Stub(r => r.RequestUri!.AbsolutePath == "/cover.png" ? Image(Png(1200, 630)) : Html(Page));
        var fetcher = new LinkPreviewFetcher(new HttpClient(stub));
        var card = await fetcher.FetchAsync("https://example.com/articles/1", CancellationToken.None);

        Assert.NotNull(card);
        Assert.Equal("https://example.com/articles/1", card!.Url);
        Assert.Equal("The Big's Title", card.Title);
        Assert.NotNull(card.Thumbnail);
        Assert.True(card.Thumbnail!.Length <= LinkPreview.MaxThumbnailBytes);
        Assert.True(ImageSniffer.TryRead(card.Thumbnail, out var info));
        Assert.Equal(info.Width, info.Height);
        Assert.True(info.Width <= LinkPreview.MaxThumbnailDimension);
        Assert.Equal(2, stub.Requested.Count);
    }

    [Fact]
    public async Task The_card_is_still_made_when_the_picture_cannot_be_had()
    {
        var stub = new Stub(r => r.RequestUri!.AbsolutePath == "/cover.png" ? new HttpResponseMessage(HttpStatusCode.NotFound) : Html(Page));
        var card = await new LinkPreviewFetcher(new HttpClient(stub)).FetchAsync("https://example.com/", CancellationToken.None);
        Assert.Equal("The Big's Title", card!.Title);
        Assert.Null(card.Thumbnail);
    }

    [Fact]
    public async Task There_is_no_card_for_a_page_with_nothing_to_show_or_something_that_is_not_a_page()
    {
        var empty = new LinkPreviewFetcher(new HttpClient(new Stub(_ => Html("<html><body>hi</body></html>"))));
        Assert.Null(await empty.FetchAsync("https://example.com/", CancellationToken.None));

        var image = new LinkPreviewFetcher(new HttpClient(new Stub(_ => Image(Png(10, 10)))));
        Assert.Null(await image.FetchAsync("https://example.com/cat.png", CancellationToken.None));

        var missing = new LinkPreviewFetcher(new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));
        Assert.Null(await missing.FetchAsync("https://example.com/", CancellationToken.None));

        var broken = new LinkPreviewFetcher(new HttpClient(new Stub(_ => throw new HttpRequestException("no route"))));
        Assert.Null(await broken.FetchAsync("https://example.com/", CancellationToken.None));
    }

    [Fact]
    public async Task Yaparrs_own_links_and_things_that_are_not_web_addresses_are_never_requested()
    {
        var stub = new Stub(_ => Html(Page));
        var fetcher = new LinkPreviewFetcher(new HttpClient(stub));
        Assert.Null(await fetcher.FetchAsync("https://yaparr.com/join#abc", CancellationToken.None));
        Assert.Null(await fetcher.FetchAsync("file:///etc/passwd", CancellationToken.None));
        Assert.Empty(stub.Requested);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:192.168.0.5", false)]
    [InlineData("93.184.216.34", true)]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946", true)]
    public void Only_addresses_on_the_public_internet_are_reached(string address, bool allowed) =>
        Assert.Equal(allowed, PublicWeb.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task A_card_sent_to_a_room_arrives_with_the_message()
    {
        var hub = new InMemoryDiscoveryHub();
        using var alice = TestUtil.NewNode(hub, "alice");
        using var bob = TestUtil.NewNode(hub, "bob");
        var ra = await alice.JoinRoomAsync("lobby");
        var rb = await bob.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => ra.Members.Count == 1 && rb.Members.Count == 1);

        var words = "have a look https://example.com/a";
        await ra.SendAsync(words, null, null, null, new LinkPreview("https://example.com/a", "Example page", "About it"));
        await TestUtil.WaitFor(() => rb.GetHistory().Any(m => m.Body == words));
        var message = rb.GetHistory().First(m => m.Body == words);
        Assert.Equal("Example page", message.Parts.Preview!.Title);
        Assert.Equal(words, message.Body);
    }

    [Fact]
    public void Every_web_address_in_the_words_is_found_where_it_is_without_the_punctuation_that_ends_a_sentence()
    {
        var text = "see https://a.example/x?y=1, and (http://b.example/page). Not ftp://c.example or www.d.example";
        var links = LinkPreview.LinksIn(text);
        Assert.Equal(new[] { "https://a.example/x?y=1", "http://b.example/page" }, links.Select(l => l.Url));
        foreach (var l in links) Assert.Equal(l.Url, text.Substring(l.Start, l.Length));
        Assert.Empty(LinkPreview.LinksIn("no links here"));
        Assert.Equal(LinkPreview.FirstLink(text), links[0].Url);
    }

    [Fact]
    public void A_page_that_could_not_be_read_still_gets_a_card_with_the_name_of_its_site()
    {
        var card = LinkPreview.Basic("https://www.thehill.com/homenews/x/")!;
        Assert.Equal("thehill.com", card.Title);
        Assert.Equal("https://www.thehill.com/homenews/x/", card.Url);
        Assert.Null(LinkPreview.Basic("not a link"));
        Assert.Null(LinkPreview.Basic("ftp://example.com/a"));
        Assert.Null(LinkPreview.Basic("https://yaparr.com/join/abc"));
    }
}
