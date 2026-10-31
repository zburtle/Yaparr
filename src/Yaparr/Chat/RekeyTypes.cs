using Yaparr.Identity;

namespace Yaparr.Chat;

/// <summary>Someone who would be given a room's new secret.</summary>
public sealed record RotationCandidate(PeerId Account, string Name, bool Online, DateTimeOffset LastSeen);

internal sealed record PreviousKey(RoomKey Key, long At);

internal sealed record RosterEntry(string Name, long LastSeen);
