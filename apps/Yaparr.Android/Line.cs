using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Yaparr.Chat;

namespace Yaparr.AndroidApp;

/// <summary>One message as the list shows it.</summary>
public sealed partial class Line : Observable
{
    private IImage? _picture;
    private string _pictureStatus = "", _replyWho = "", _replyText = "";

    public Line(string id, string who, string text, DateTimeOffset when, bool mine, string? channel = null, ChatMessage? source = null)
    {
        Id = id;
        Who = who;
        Text = text;
        When = when;
        Time = when.LocalDateTime.ToString("HH:mm");
        Mine = mine;
        Channel = channel;
        Source = source;
        Attachment = source?.Attachment;
    }

    /// <summary>The account that wrote the message (its picture is looked up by it).</summary>
    public string? Account => Source?.From.Account.Value;

    public IBrush WhoTint => AvatarBrushes.For(Account) ?? Theme.TintFor(Who);

    public string WhoInitial => AvatarBrushes.For(Account) != null ? "" : Who.Length == 0 ? "?" : new System.Globalization.StringInfo(Who).SubstringByTextElements(0, 1).ToUpperInvariant();

    public void AvatarChanged() { Raise(nameof(WhoTint)); Raise(nameof(WhoInitial)); }

    /// <summary>The key of the message this one answers, if it answers one.</summary>
    public string? ReplyKey { get; set; }

    /// <summary>The people this message names.</summary>
    public IReadOnlyList<Mention> Mentions { get; set; } = Array.Empty<Mention>();

    /// <summary>This message names you (or everyone, from an admin).</summary>
    public bool MentionsMe { get; set; }

    public bool HasReply => ReplyKey != null;

    /// <summary>Who wrote the message this one answers; empty until that message is found.</summary>
    public string ReplyWho
    {
        get => _replyWho;
        set { if (_replyWho == value) return; _replyWho = value; Raise(); }
    }

    /// <summary>The start of the message this one answers, or that it is not on this phone.</summary>
    public string ReplyText
    {
        get => _replyText;
        set { if (_replyText == value) return; _replyText = value; Raise(); }
    }

    /// <summary>A short form of the message for the reply bar and the quote under a reply.</summary>
    public string Preview => Attachment != null ? (Text.Length == 0 ? "Picture" : "Picture: " + Text) : Text.Length > 120 ? Text[..120] + "\u2026" : Text;


    public string Id { get; }
    public string Who { get; }
    public string Text { get; }
    public DateTimeOffset When { get; }
    public string Time { get; }
    public bool Mine { get; }

    /// <summary>The text channel of a room message; null for a direct message.</summary>
    public string? Channel { get; }

    /// <summary>The message as the library has it; null for one just sent from this phone.</summary>
    public ChatMessage? Source { get; set; }

    /// <summary>The picture this message carries, if it carries one.</summary>
    public Attachment? Attachment { get; set; }

    /// <summary>The card the sender attached for a link in the words, shown below them.</summary>
    public LinkPreview? Card { get; set; }
    public bool HasCard => Card != null;
    public string CardHost => Card?.Host ?? "";
    public string CardTitle => Card?.Title is { Length: > 0 } t ? t : Card?.Host ?? "";
    public string CardDescription => Card?.Description ?? "";
    public bool HasCardDescription => CardDescription.Length > 0;
    public bool HasCardImage => CardImage != null;

    private IImage? _cardImage;
    private bool _cardImageTried;

    /// <summary>The small picture of the card, once it has been read from the message.</summary>
    public IImage? CardImage
    {
        get
        {
            if (_cardImageTried || Card?.Thumbnail is not { Length: > 0 } bytes) return _cardImage;
            _cardImageTried = true;
            try { using var ms = new MemoryStream(bytes); _cardImage = new Bitmap(ms); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { }
            return _cardImage;
        }
    }

    public bool HasAttachment => Attachment != null;

    /// <summary>Text under the picture is its caption; a message that is only a picture has none.</summary>
    public bool HasText => Text.Length > 0;

    /// <summary>The picture itself, once it is on the phone.</summary>
    public IImage? Picture
    {
        get => _picture;
        set { _picture = value; Raise(); Raise(nameof(HasPicture)); Raise(nameof(HasPictureStatus)); }
    }

    public bool HasPicture => _picture != null;

    /// <summary>The bytes of the picture when it is an animated GIF, so that it can be played (the still above is its first frame).</summary>
    public byte[]? Animation
    {
        get => _animation;
        set { _animation = value; Raise(); }
    }

    private byte[]? _animation;

    /// <summary>What is happening to a picture that is not here yet ("Loading…", "Tap to download (3 MB)").</summary>
    public string PictureStatus
    {
        get => _pictureStatus;
        set { if (_pictureStatus == value) return; _pictureStatus = value; Raise(); Raise(nameof(HasPictureStatus)); }
    }

    public bool HasPictureStatus => _picture == null && Attachment != null;

    /// <summary>The picture's tiny preview from the message, shown until the picture arrives.</summary>
    public IImage? Thumbnail
    {
        get
        {
            if (_thumbnailTried || Attachment?.Thumbnail is not { Length: > 0 } bytes) return _thumbnail;
            _thumbnailTried = true;
            try { using var ms = new MemoryStream(bytes); _thumbnail = new Bitmap(ms); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { }
            return _thumbnail;
        }
    }

    private IImage? _thumbnail;
    private bool _thumbnailTried;

    /// <summary>The line for the chat list: the words, or that it is a picture.</summary>
    public string PreviewText => Attachment == null ? Text : Text.Length == 0 ? "Picture" : "Picture: " + Text;

    public HorizontalAlignment Align => Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public IBrush Fill => MentionsMe && !Mine ? Resource("Mention") : Resource(Mine ? "BubbleMine" : "BubbleOther");
    public IBrush Ink => Mine ? Brushes.White : Resource("Ink");

    private static IBrush Resource(string key) =>
        Avalonia.Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : Brushes.Gray;
}
