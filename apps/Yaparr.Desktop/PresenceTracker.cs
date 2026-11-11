namespace Yaparr.Desktop;

/// <summary>
/// Remembers who (or which shares) were there at the last look, and says how many came and went since. Used for the sounds around sharing:
/// a share starting or ending, and people starting or stopping watching yours.
/// </summary>
public sealed class PresenceTracker
{
    private HashSet<string> _known;

    /// <param name="already">Who is there now, and so is not announced when they are first seen.</param>
    public PresenceTracker(IEnumerable<string>? already = null) => _known = new HashSet<string>(already ?? Array.Empty<string>(), StringComparer.Ordinal);

    /// <summary>Takes the new look; returns who is new and who is gone since the last one.</summary>
    public (IReadOnlyList<string> Arrived, IReadOnlyList<string> Gone) Update(IEnumerable<string> now)
    {
        var current = new HashSet<string>(now, StringComparer.Ordinal);
        var arrived = current.Where(x => !_known.Contains(x)).ToList();
        var gone = _known.Where(x => !current.Contains(x)).ToList();
        _known = current;
        return (arrived, gone);
    }
}
