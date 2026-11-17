using System.Collections.Concurrent;
using Yaparr.Identity;

namespace Yaparr.Chat;

// Who uses Yaparr Cloud, so the apps can show a small cloud next to their name. A device says so in its hello (and again when it signs in or out), and passes on what it
// knows of the members it is connected to in the lists of links, so it reaches members that are not connected to you. It is what a device says about itself, not a proof:
// nothing depends on it and it unlocks nothing, it only tells you that the person has signed in to a server.
public sealed partial class ChatNode
{
    private const int MaxCloudDevices = 5000;

    private readonly ConcurrentDictionary<string, byte> _cloudDevices = new();

    /// <summary>Who uses Yaparr Cloud changed (someone signed in or out, or was heard of): the apps show the cloud again.</summary>
    public event Action? CloudUsersChanged;

    /// <summary>Whether this person (an account) has said that they use Yaparr Cloud: signed in to a server, as this device is when <see cref="MailboxEnabled"/> is on.</summary>
    public bool UsesCloud(PeerId account)
    {
        if (account == AccountId && MailboxEnabled) return true;
        foreach (var device in _cloudDevices.Keys)
            if (AccountOf(new PeerId(device)) == account) return true;
        return false;
    }

    /// <summary>The same for one device (a connection or a member of a voice channel): whether the account it belongs to uses Yaparr Cloud.</summary>
    public bool UsesCloudDevice(PeerId device) => UsesCloud(AccountOf(device));

    private void NoteCloudDevice(PeerId device, bool on)
    {
        bool changed = on
            ? _cloudDevices.Count < MaxCloudDevices && _cloudDevices.TryAdd(device.Value, 0)
            : _cloudDevices.TryRemove(device.Value, out _);
        if (changed) CloudUsersChanged?.Invoke();
    }

    // This device signed in to a server, or out: connected peers are told, and the apps show it for ourselves.
    private void AnnounceCloud()
    {
        foreach (var peer in _peers.Values.Where(p => p.Name != null))
            Send(peer.Connection, new Wire { Type = Wire.Cloud, Cld = MailboxEnabled });
        CloudUsersChanged?.Invoke();
    }
}
