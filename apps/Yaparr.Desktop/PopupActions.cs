namespace Yaparr.Desktop;

/// <summary>
/// What the person can do from a pop-up, as the desktop offers it: click it (or its Reply button, where there is no box to type in) to open the chat, type a reply into
/// it where the desktop has a box for that (KDE), or mute the room. Each runs on whatever thread the desktop's answer came on.
/// </summary>
internal sealed record PopupActions(Action Open, Action<string> Reply, Action? Mute = null);

/// <summary>
/// Which pop-ups are still showing and what each can do, by the number the desktop gave it, so that an answer from the desktop (a button, a typed reply) reaches the
/// right chat. A new pop-up that replaces an old one has the old one's number, so it takes its place. At most <see cref="Max"/> are kept: the oldest are dropped.
/// </summary>
internal sealed class PopupRegistry
{
    public const int Max = 32;

    private readonly object _gate = new();
    private readonly Dictionary<uint, PopupActions> _byId = new();
    private readonly Queue<uint> _order = new();

    public void Add(uint id, PopupActions actions)
    {
        lock (_gate)
        {
            if (!_byId.ContainsKey(id)) _order.Enqueue(id);
            _byId[id] = actions;
            while (_byId.Count > Max && _order.Count > 0) _byId.Remove(_order.Dequeue());
        }
    }

    public void Remove(uint id)
    {
        lock (_gate) _byId.Remove(id);
    }

    /// <summary>The desktop says an action of a pop-up was used: <c>default</c> (a click), <c>reply</c> (the Reply button), <c>inline-reply</c> or <c>mute</c>. False if the pop-up is not known.</summary>
    public bool Invoke(uint id, string action)
    {
        PopupActions? actions;
        lock (_gate) _byId.TryGetValue(id, out actions);
        if (actions == null) return false;
        switch (action)
        {
            case "mute": if (actions.Mute is { } mute) mute(); break;
            default: actions.Open(); break;   // a click, the Reply button without a box, anything else the desktop sends: the chat opens
        }
        return true;
    }

    /// <summary>The person typed a reply into the pop-up (KDE's box). Empty text sends nothing.</summary>
    public bool Reply(uint id, string text)
    {
        PopupActions? actions;
        lock (_gate) _byId.TryGetValue(id, out actions);
        text = (text ?? "").Trim();
        if (actions == null || text.Length == 0) return false;
        actions.Reply(text);
        return true;
    }
}
