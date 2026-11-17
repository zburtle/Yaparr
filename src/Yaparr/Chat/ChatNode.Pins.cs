namespace Yaparr.Chat;

/// <summary>A room or direct chat the account has pinned: which kind, its id (the room's <see cref="ChatRoom.RoomId"/>, or the other person's id for a direct chat) and where it goes among the pinned ones.</summary>
public sealed record PinnedConversation(bool IsRoom, string Id, string Order);

// Pinned rooms and chats stay at the top of the lists, in an order the person sets, the same on all their devices. Each pin is an entry of the account of its own
// (pin:r:<room id> or pin:d:<person id>) whose value is its position among the pinned ones (see PinOrder), and an unpin is the entry marked deleted, so the usual
// rule decides between devices: the newer write of a pin wins, and moving one pinned conversation never touches the others.
public sealed partial class ChatNode
{
    private const string PinPrefix = "pin:";
    private const int MaxPinIdLength = 80;

    /// <summary>The pins changed: made here, or heard from another of your devices (or the account's cloud copy). Read <see cref="Pins"/> again.</summary>
    public event Action? PinsChanged;

    /// <summary>The pinned rooms and chats, first to last. Some may be of rooms or chats this device does not have (yet); whoever shows them leaves those out.</summary>
    public IReadOnlyList<PinnedConversation> Pins
    {
        get
        {
            lock (_acctGate)
                return _acct.Values
                    .Where(e => !e.Deleted && e.Key.StartsWith(PinPrefix, StringComparison.Ordinal))
                    .Select(ParsePin).OfType<PinnedConversation>()
                    .OrderBy(p => p.Order, StringComparer.Ordinal).ThenBy(p => p.IsRoom ? 0 : 1).ThenBy(p => p.Id, StringComparer.Ordinal)
                    .ToList();
        }
    }

    /// <summary>Whether the room (<paramref name="isRoom"/>) or direct chat with this id is pinned.</summary>
    public bool IsConversationPinned(bool isRoom, string id)
    {
        lock (_acctGate) return HeldPin(isRoom, id) is { Deleted: false };
    }

    /// <summary>Pins a room or direct chat, after the ones pinned already. Nothing happens if it is pinned.</summary>
    public void Pin(bool isRoom, string id)
    {
        CheckPinId(id);
        string order;
        lock (_acctGate)
        {
            if (HeldPin(isRoom, id) is { Deleted: false }) return;
            string? last = _acct.Values
                .Where(e => !e.Deleted && e.Key.StartsWith(PinPrefix, StringComparison.Ordinal))
                .Select(ParsePin).OfType<PinnedConversation>()
                .Select(p => p.Order).OrderBy(o => o, StringComparer.Ordinal).LastOrDefault();
            order = PinOrder.Between(last, null);
        }
        Stamp(PinKey(isRoom, id), order, deleted: false);
    }

    /// <summary>Takes a room or direct chat off the pinned ones. Nothing happens if it is not pinned.</summary>
    public void Unpin(bool isRoom, string id)
    {
        CheckPinId(id);
        string? held;
        lock (_acctGate)
        {
            if (HeldPin(isRoom, id) is not { Deleted: false } pin) return;
            held = pin.Value;
        }
        Stamp(PinKey(isRoom, id), held, deleted: true);
    }

    /// <summary>
    /// Moves a pinned room or chat between two others, given the positions of the one it should come after and the one it should come before (null at an end), as
    /// shown by whoever asks. Only this one pin changes. Nothing happens if it is not pinned.
    /// </summary>
    public void MovePin(bool isRoom, string id, string? after, string? before)
    {
        CheckPinId(id);
        if (after != null && !PinOrder.IsValid(after) || before != null && !PinOrder.IsValid(before)) throw new ArgumentException("Not a pin position.");
        string order;
        lock (_acctGate)
        {
            if (HeldPin(isRoom, id) is not { Deleted: false } pin) return;
            // Two devices pinned at the same moment and now share a position, or what was shown has changed since: go after the first one given.
            if (after != null && before != null && string.CompareOrdinal(after, before) >= 0) before = null;
            order = PinOrder.Between(after, before);
            if (order == pin.Value) return;
        }
        Stamp(PinKey(isRoom, id), order, deleted: false);
    }

    private static string PinKey(bool isRoom, string id) => PinPrefix + (isRoom ? "r:" : "d:") + id;

    private AccountEntry? HeldPin(bool isRoom, string id) => _acct.GetValueOrDefault(PinKey(isRoom, id));

    private static bool IsPinId(string id) => id.Length is > 0 and <= MaxPinIdLength && id.All(char.IsAsciiHexDigit);

    private static void CheckPinId(string id)
    {
        if (!IsPinId(id)) throw new ArgumentException("Not the id of a room or a person.", nameof(id));
    }

    private static PinnedConversation? ParsePin(AccountEntry e)
    {
        var rest = e.Key[PinPrefix.Length..];
        if (rest.Length < 3 || rest[1] != ':' || rest[0] is not ('r' or 'd') || !IsPinId(rest[2..]) || !PinOrder.IsValid(e.Value)) return null;
        return new PinnedConversation(rest[0] == 'r', rest[2..], e.Value!);
    }
}
