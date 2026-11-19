using Yaparr.Chat;

namespace Yaparr.AppCore;

/// <summary>The history of rooms and conversations as <see cref="IPieceSource"/>: a piece for each month of each (see <see cref="HistoryPieces"/>).</summary>
public sealed class HistoryPieceSource(IChatHistoryStore store) : IPieceSource
{
    public IReadOnlyDictionary<string, string> Local() => HistoryPieces.Local(store);

    public byte[]? Build(string name) => HistoryPieces.Build(store, name);

    public bool Take(string name, byte[] content) => HistoryPieces.Take(store, name, content) >= 0;
}
