using Yaparr.Identity;

namespace Yaparr.Chat;

public sealed partial class ChatNode
{
    private const int MaxNameLength = 64;

    private static string CleanName(string? name)
    {
        // No control characters and no text-direction overrides, which can make one name pass for another.
        var clean = new string((name ?? "").Where(c => !char.IsControl(c) && c is not ((>= '‪' and <= '‮') or (>= '⁦' and <= '⁩') or '‎' or '‏')).ToArray()).Trim();
        return clean.Length <= MaxNameLength ? clean : clean[..MaxNameLength].TrimEnd();
    }

    /// <summary>
    /// Changes the name others see. Everyone you are connected to is told at once, your presence in each room and in
    /// the voice channel is announced again under the new name, and new messages carry it. Messages already sent keep
    /// the name they were signed with.
    /// </summary>
    public void Rename(string newName)
    {
        var name = CleanName(newName);
        if (name.Length == 0) throw new ArgumentException("A name cannot be empty.", nameof(newName));
        if (name == _displayName) return;
        ApplyName(name);
        // Your other devices take the new name too.
        Stamp(NameKey, name, deleted: false);
    }

    private void ApplyName(string name)
    {
        _displayName = name;
        _options.Log?.Invoke($"renamed to {name}");

        foreach (var peer in _peers.Values)
            Send(peer.Connection, new Wire { Type = Wire.Rename, Name = name });
        foreach (var room in _rooms.Values)
        {
            SendPresence(room);
            if (room.ActiveVoice != null) PublishVoiceState(room, active: true, room.SelfMuted, room.SelfDeafened);
        }
    }

    // Someone we are connected to changed their name. They can only change their own name here: the connection proves who they are.
    private void OnRenamed(PeerState peer, string newName)
    {
        var name = CleanName(newName);
        if (name.Length == 0 || name == peer.Name) return;
        peer.Name = name;
        var info = peer.Info;
        _options.Log?.Invoke($"{peer.Id.Short} is now called {name}");
        foreach (var room in _rooms.Values)
            if (room.MemberStates.ContainsKey(peer.Id)) room.RaiseRenamed(info);
        PeerRenamed?.Invoke(info);
    }
}
