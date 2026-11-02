using Yaparr.Identity;

namespace Yaparr.Chat;

/// <summary>Someone who would be given a room's new secret.</summary>
/// <summary>Someone seen in a room: who, the name they last used, whether they are here now, and when they were last seen.</summary>
public sealed record RoomPerson(PeerId Account, string Name, bool Online, DateTimeOffset LastSeen);

public sealed record RotationCandidate(PeerId Account, string Name, bool Online, DateTimeOffset LastSeen);

internal sealed record PreviousKey(RoomKey Key, long At);

internal sealed record RosterEntry(string Name, long LastSeen);
