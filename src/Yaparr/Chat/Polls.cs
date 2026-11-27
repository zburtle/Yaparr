using Yaparr.Identity;

namespace Yaparr.Chat;

/// <summary>
/// A question with a few answers to choose from, asked in a message (see <see cref="MessageEnvelope.Wrap"/>). A vote is a reaction on that message with the
/// number of the answer as its emoji (1️⃣ for the first, 2️⃣ for the second, and so on), so votes travel, are kept and are handed to a device that was away exactly
/// as reactions are, and a version that does not know polls shows the question with its numbered answers and the votes as the reactions they are.
/// </summary>
public sealed record Poll(string Question, IReadOnlyList<string> Options, bool Multiple = false)
{
    public const int MinOptions = 2;
    public const int MaxOptions = 10;
    public const int MaxQuestionLength = 200;
    public const int MaxOptionLength = 100;

    // The emoji that name the answers, as the emoji set writes them (so that a reaction with one is a good reaction).
    private static readonly string[] Written = { "1️⃣", "2️⃣", "3️⃣", "4️⃣", "5️⃣", "6️⃣", "7️⃣", "8️⃣", "9️⃣", "\U0001F51F" };

    /// <summary>The emoji of the answers, in order: the vote for answer <c>i</c> is the reaction <c>Keycaps[i]</c>.</summary>
    public static IReadOnlyList<string> Keycaps { get; } = Written.Select(w => EmojiData.TryNormalize(w, out var e) ? e : w).ToArray();

    /// <summary>The poll with its words trimmed and put on one line each, empty answers left out and the limits kept; null when no poll is left (no question, or fewer than two answers).</summary>
    public static Poll? Clean(string? question, IEnumerable<string?>? options, bool multiple)
    {
        static string One(string? s, int max)
        {
            var line = string.Join(' ', (s ?? "").Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
            line = new string(line.Where(c => !char.IsControl(c)).ToArray());
            return line.Length <= max ? line : line[..max].TrimEnd();
        }
        var q = One(question, MaxQuestionLength);
        var o = (options ?? Array.Empty<string?>()).Select(x => One(x, MaxOptionLength)).Where(x => x.Length > 0).Take(MaxOptions).ToArray();
        return q.Length == 0 || o.Length < MinOptions ? null : new Poll(q, o, multiple);
    }

    /// <summary>What the message says in words: the question, then the answers by number. This is what a version without polls shows, and what a notice or a quote of the poll uses.</summary>
    public string Words => "\U0001F4CA " + Question + "\n" + string.Join("\n", Options.Select((o, i) => Keycaps[i] + " " + o));

    /// <summary>The emoji of answer <paramref name="index"/>.</summary>
    public string KeycapOf(int index) => Keycaps[index];

    /// <summary>What a poll comes to, from the reactions on its message.</summary>
    public PollResult Tally(IReadOnlyList<ReactionSummary> reactions)
    {
        // For each person, the answers they chose; a poll with one choice counts the lowest of them, so every device comes to the same answer.
        var choices = new Dictionary<PeerId, List<int>>();
        for (int i = 0; i < Options.Count; i++)
            foreach (var summary in reactions.Where(r => r.Emoji == Keycaps[i]))
                foreach (var account in summary.Accounts)
                {
                    if (!choices.TryGetValue(account, out var list)) choices[account] = list = new List<int>();
                    list.Add(i);
                }
        var votes = Options.Select(_ => new List<PeerId>()).ToArray();
        foreach (var (account, list) in choices)
            foreach (var i in Multiple ? list : new List<int> { list.Min() })
                votes[i].Add(account);
        return new PollResult(this, votes.Select((v, i) => new PollOptionResult(i, Options[i], v.OrderBy(a => a.Value, StringComparer.Ordinal).ToList())).ToList(), choices.Count);
    }
}

/// <summary>One answer of a poll and who chose it (accounts).</summary>
public sealed record PollOptionResult(int Index, string Text, IReadOnlyList<PeerId> Accounts)
{
    public int Count => Accounts.Count;
}

/// <summary>What a poll comes to: each answer with its votes, and how many people voted.</summary>
public sealed record PollResult(Poll Poll, IReadOnlyList<PollOptionResult> Options, int Voters);
