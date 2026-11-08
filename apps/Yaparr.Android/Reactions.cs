using System.Collections.ObjectModel;
using Avalonia.Media;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.AndroidApp;

/// <summary>One emoji under a message: the emoji, how many put it there, and whether you did. The button that adds another one is <see cref="IsAdd"/>.</summary>
public sealed class ReactionChip : Observable
{
    public ReactionChip(Line owner, string emoji, int count, bool mine, bool isAdd = false)
    {
        Owner = owner;
        Emoji = emoji;
        Count = count;
        Mine = mine;
        IsAdd = isAdd;
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
    public IBrush Ink => Owner.Ink;
}

public sealed partial class Line
{
    private string _reactionSignature = "";

    /// <summary>The emoji on this message; the last one, when there are any, is the button to add another.</summary>
    public ObservableCollection<ReactionChip> Reactions { get; } = new();

    public bool HasReactions => Reactions.Count > 0;

    /// <summary>Shows what the reactions on this message come to now. Nothing changes on screen when they are as they were.</summary>
    public void SetReactions(IReadOnlyList<ReactionSummary> reactions, PeerId me)
    {
        var signature = string.Join("|", reactions.Select(r => r.Emoji + ":" + r.Count + ":" + r.Accounts.Contains(me)));
        if (signature == _reactionSignature) return;
        _reactionSignature = signature;
        Reactions.Clear();
        foreach (var r in reactions) Reactions.Add(new ReactionChip(this, r.Emoji, r.Count, r.Accounts.Contains(me)));
        if (reactions.Count > 0) Reactions.Add(new ReactionChip(this, "☺️", 0, mine: false, isAdd: true));
        Raise(nameof(HasReactions));
    }
}
