using Yaparr.Identity;

namespace Yaparr.Chat;

public sealed partial class ChatNode
{
    /// <summary>Where this node keeps messages (rooms and direct conversations), if it was given a store; what is kept in sync is read from and put back into it.</summary>
    public IChatHistoryStore? KeptHistory => _options.HistoryStore;

    /// <summary>
    /// Rows were taken into the store from the account's kept copy, or removed by it: the rooms and conversations it names (a room's key, a person's id) raise
    /// <see cref="ChatRoom.HistoryChanged"/> / <see cref="DirectChat.HistoryChanged"/>, so what they show is read again. Called from any thread.
    /// </summary>
    public void HistoryChangedBySync(IEnumerable<(bool Direct, string Who)> changed)
    {
        foreach (var (direct, who) in changed.Distinct())
        {
            if (direct) { if (who.Length == 64 && _directChats.TryGetValue(new PeerId(who), out var chat)) chat.RaiseHistoryChanged(); }
            else if (_rooms.TryGetValue(who, out var room)) room.RaiseHistoryChanged();
        }
    }
}
