using System.Collections.ObjectModel;
using Avalonia.Media;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.AndroidApp;

/// <summary>One emoji under a message: the emoji, how many put it there, and whether you did. The button that adds another one is <see cref="IsAdd"/>.</summary>
public sealed class ReactionChip : Observable
{
    public ReactionChip(Line owner, string emoji, int count, bool mine, bool isAdd = false, CustomEmoji? custom = null)
    {
        Owner = owner;
        Emoji = emoji;
        Count = count;
        Mine = mine;
        IsAdd = isAdd;
        Custom = custom;
    }

    /// <summary>The custom emoji this reaction is (a room's or someone's own), or null for a standard emoji.</summary>
    public CustomEmoji? Custom { get; }

    /// <summary>The picture of a custom emoji, once it is on this phone.</summary>
    public IImage? Picture => Custom == null ? null : EmojiPictures.For(Custom);
    public bool ShowPicture => !IsAdd && Picture != null;

    /// <summary>The emoji as text: the emoji itself, or for a custom one its :name: until its picture has arrived.</summary>
    public string EmojiText => Custom != null ? ":" + Custom.Name + ":" : Emoji;
    public bool ShowEmojiText => !IsAdd && Picture == null;

    /// <summary>The picture of this custom emoji arrived: the chip is drawn again with it.</summary>
    public void RaisePicture()
    {
        Raise(nameof(Picture));
        Raise(nameof(ShowPicture));
        Raise(nameof(ShowEmojiText));
    }

    public Line Owner { get; }
    public string Emoji { get; }
    public int Count { get; }
    public bool Mine { get; }
    public bool IsAdd { get; }
    public bool ShowCount => !IsAdd;
    public bool ShowEmoji => !IsAdd;
    public string CountText => Count.ToString();

    /// <summary>Yours have an edge in the accent colour.</summary>
    public IBrush Edge => Mine && !IsAdd ? Theme.Brush("Accent") : Brushes.Transparent;
    // The button that adds another is not a chip: only a small grey circle with a plus in it.
    public IBrush Fill => IsAdd ? Brushes.Transparent : new SolidColorBrush(Mine ? Color.FromArgb(0x44, 0x80, 0x80, 0x80) : Color.FromArgb(0x26, 0x80, 0x80, 0x80));
    public IBrush Ink => Theme.Brush("Ink");
}

public sealed partial class Line
{
    private string _reactionSignature = "";

    /// <summary>The emoji on this message; the last one, when there are any, is the button to add another.</summary>
    public ObservableCollection<ReactionChip> Reactions { get; } = new();

    public bool HasReactions => Reactions.Count > 0;

    /// <summary>The picture of a custom emoji used as a reaction on this message arrived: those chips are drawn again.</summary>
    public void RaiseReactionPictures(string hash)
    {
        foreach (var chip in Reactions.Where(c => c.Custom?.Hash == hash).ToList()) chip.RaisePicture();
    }

    /// <summary>Shows what the reactions on this message come to now. Nothing changes on screen when they are as they were.</summary>
    public void SetReactions(IReadOnlyList<ReactionSummary> reactions, PeerId me)
    {
        var signature = string.Join("|", reactions.Select(r => r.Emoji + ":" + r.Count + ":" + r.Accounts.Contains(me)));
        if (signature == _reactionSignature) return;
        _reactionSignature = signature;
        Reactions.Clear();
        foreach (var r in reactions) Reactions.Add(new ReactionChip(this, r.Emoji, r.Count, r.Accounts.Contains(me), custom: r.Custom));
        if (reactions.Count > 0) Reactions.Add(new ReactionChip(this, "☺️", 0, mine: false, isAdd: true));
        Raise(nameof(HasReactions));
    }
}
