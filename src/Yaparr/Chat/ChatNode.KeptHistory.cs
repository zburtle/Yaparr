namespace Yaparr.Chat;

public sealed partial class ChatNode
{
    /// <summary>Where this node keeps messages (rooms and direct conversations), if it was given a store; what is kept in sync is read from and put back into it.</summary>
    public IChatHistoryStore? KeptHistory => _options.HistoryStore;
}
