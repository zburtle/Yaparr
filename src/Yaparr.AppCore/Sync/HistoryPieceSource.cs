using Yaparr.Chat;

namespace Yaparr.AppCore;

/// <summary>The history of rooms and conversations as <see cref="IPieceSource"/>: a piece for each month of each (see <see cref="HistoryPieces"/>).</summary>
/// <param name="taken">Told, for each piece taken in that changed anything, its name and how many rows it added or removed.</param>
public sealed class HistoryPieceSource(IChatHistoryStore store, Action<string, int>? taken = null) : IPieceSource
{
    public IReadOnlyDictionary<string, string> Local() => HistoryPieces.Local(store);

    public byte[]? Build(string name) => HistoryPieces.Build(store, name);

    public bool Take(string name, byte[] content)
    {
        var changed = HistoryPieces.Take(store, name, content);
        if (changed > 0) taken?.Invoke(name, changed);
        return changed >= 0;
    }
}
