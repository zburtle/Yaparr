using System.Collections.ObjectModel;
using Avalonia.Media;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.AndroidApp;

/// <summary>One answer of a poll in a message: its words, how many chose it, the share of the people who voted, and whether you did.</summary>
public sealed class PollOption
{
    public PollOption(Line owner, int index, string text, int count, double percent, bool mine)
    {
        Owner = owner; Index = index; Text = text; Count = count; Percent = percent; Mine = mine;
    }

    public Line Owner { get; }
    public int Index { get; }
    public string Text { get; }
    public int Count { get; }
    public double Percent { get; }
    public bool Mine { get; }
    public string Label => Mine ? "✓ " + Text : Text;
    public FontWeight LabelWeight => Mine ? FontWeight.SemiBold : FontWeight.Normal;
    public string CountText => Count == 0 ? "" : Count.ToString();
    public IBrush Ink => Owner.Ink;
}

// A poll: a question with answers to vote on (see Poll in the library; a vote is a reaction with the number of the answer).
public sealed partial class Line
{
    /// <summary>The poll this message asks, or null.</summary>
    public Poll? Poll { get; init; }
    public bool HasPoll => Poll != null;
    public string PollQuestion => Poll?.Question ?? "";

    /// <summary>The words are shown as they are unless the message asks a poll (that is shown as its question and answers).</summary>
    public bool HasPlainText => HasText && Poll == null;

    public ObservableCollection<PollOption> PollOptions { get; } = new();

    private string _pollHint = "";
    public string PollHint
    {
        get => _pollHint;
        private set { if (_pollHint == value) return; _pollHint = value; Raise(); }
    }

    /// <summary>The poll as the votes stand.</summary>
    private void SetPoll(IReadOnlyList<ReactionSummary> reactions, PeerId me)
    {
        if (Poll == null) return;
        var result = Poll.Tally(reactions);
        PollOptions.Clear();
        foreach (var o in result.Options)
            PollOptions.Add(new PollOption(this, o.Index, o.Text, o.Count, result.Voters == 0 ? 0 : 100.0 * o.Count / result.Voters, o.Accounts.Contains(me)));
        PollHint = (Poll.Multiple ? "Choose any number" : "Choose one") + " · " + (result.Voters == 1 ? "1 person has voted" : result.Voters + " people have voted");
    }
}
