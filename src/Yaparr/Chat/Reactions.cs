using Yaparr.Identity;

namespace Yaparr.Chat;

/// <summary>
/// One person putting an emoji on a message, or taking it off again. It travels as a message of its own (see <see cref="MessageEnvelope.WrapReaction"/>),
/// so it is signed, passed on, kept in the history and handed to a device that was away like any message; it is not shown as one.
/// </summary>
/// <param name="Target">The <see cref="ChatMessage.Key"/> of the message it is on.</param>
/// <param name="Account">From a linked device: the account key it belongs to.</param>
/// <param name="Certificate">From a linked device: the account's certificate for it, which proves that.</param>
public sealed record ReactionEvent(string Target, string Emoji, bool On, string? Account = null, string? Certificate = null);

/// <summary>One emoji on a message, and who put it there (accounts: a person with several devices counts once).</summary>
public sealed record ReactionSummary(string Emoji, IReadOnlyList<PeerId> Accounts)
{
    public int Count => Accounts.Count;
}

/// <summary>The reactions of a conversation: for each message (by <see cref="ChatMessage.Key"/>), its emoji with who put them.</summary>
public sealed class ReactionSet
{
    public static readonly ReactionSet Empty = new(new Dictionary<string, IReadOnlyList<ReactionSummary>>());

    private readonly IReadOnlyDictionary<string, IReadOnlyList<ReactionSummary>> _byTarget;

    internal ReactionSet(IReadOnlyDictionary<string, IReadOnlyList<ReactionSummary>> byTarget) => _byTarget = byTarget;

    /// <summary>The emoji on a message, in the order they were first put there; empty when there are none.</summary>
    public IReadOnlyList<ReactionSummary> For(string messageKey) =>
        _byTarget.TryGetValue(messageKey, out var list) ? list : Array.Empty<ReactionSummary>();

    /// <summary>Whether this account has put this emoji on the message.</summary>
    public bool Has(string messageKey, string emoji, PeerId account) =>
        For(messageKey).Any(r => r.Emoji == emoji && r.Accounts.Contains(account));

    /// <summary>The messages that have any reaction.</summary>
    public IEnumerable<string> Targets => _byTarget.Keys;
}

internal static class ReactionTally
{
    /// <summary>
    /// What the reactions in some messages come to. For each person, message and emoji the latest event counts (put on, or taken off); events with an
    /// emoji that is not in the set, or without a message to be on, are ignored.
    /// </summary>
    /// <param name="messages">Messages as kept: their key, who sent them (a device), the time and the text; any that are not reactions are skipped.</param>
    /// <param name="accountOf">The person a device belongs to.</param>
    /// <param name="skip">People whose reactions are not counted (blocked or banned).</param>
    public static ReactionSet Build(IEnumerable<(string Key, string From, long Ts, string Text)> messages, Func<string, PeerId> accountOf, Func<PeerId, bool> skip)
    {
        var latest = new Dictionary<(string Target, PeerId Account, string Emoji), (long Ts, string Key, bool On)>();
        var firstOn = new Dictionary<(string Target, string Emoji), long>();
        foreach (var (key, from, ts, text) in messages)
        {
            if (!MessageEnvelope.TryReadReaction(text, out var reaction)) continue;
            var account = accountOf(from);
            if (skip(account)) continue;
            var id = (reaction.Target, account, reaction.Emoji);
            // The later event wins; at the same moment the key decides, so every device comes to the same answer.
            if (latest.TryGetValue(id, out var old) && !(ts > old.Ts || ts == old.Ts && string.CompareOrdinal(key, old.Key) > 0)) continue;
            latest[id] = (ts, key, reaction.On);
            if (reaction.On && (!firstOn.TryGetValue((reaction.Target, reaction.Emoji), out var seen) || ts < seen)) firstOn[(reaction.Target, reaction.Emoji)] = ts;
        }

        var byTarget = new Dictionary<string, IReadOnlyList<ReactionSummary>>();
        foreach (var target in latest.Where(kv => kv.Value.On).GroupBy(kv => kv.Key.Target))
        {
            var summaries = target.GroupBy(kv => kv.Key.Emoji)
                .OrderBy(g => firstOn.GetValueOrDefault((target.Key, g.Key)))
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new ReactionSummary(g.Key, g.Select(kv => kv.Key.Account).OrderBy(a => a.Value, StringComparer.Ordinal).ToList()))
                .ToList();
            byTarget[target.Key] = summaries;
        }
        return new ReactionSet(byTarget);
    }
}
