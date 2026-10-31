using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Yaparr.Identity;

namespace Yaparr.Chat;

public static class ChannelIds
{
    public const byte Chat = 1;
    public const byte File = 3;
    public const byte Voice = 16;
    public const byte Media = 17;
}

/// <summary>
/// Someone in a room. <see cref="Id"/> is one device; <see cref="Account"/> is the person, the same for all of their
/// devices. Group by <see cref="Account"/> to show people, and compare it with <see cref="ChatNode.AccountId"/> to
/// recognise yourself on another device.
/// </summary>
public sealed record PeerInfo(PeerId Id, string DisplayName, IPEndPoint? EndPoint, bool IsRelayed = false, PeerId? AccountId = null)
{
    public PeerId Account => AccountId ?? Id;
}

public enum DeliveryState
{
    /// <summary>Sent; waiting for the other side to confirm.</summary>
    Sending,
    /// <summary>Every connected recipient confirmed receipt.</summary>
    Delivered,
    /// <summary>Some recipients confirmed, others did not within the timeout.</summary>
    PartlyDelivered,
    /// <summary>Nobody confirmed within the timeout. It is re-sent automatically when the peer reconnects, or on retry.</summary>
    NotDelivered,
    /// <summary>Nobody was connected to receive it. It is handed over when someone connects.</summary>
    Queued,
}

public sealed record DeliveryStatus(string MessageId, DeliveryState State, int Delivered, int Total);

public sealed record ChatMessage(PeerInfo From, string? Room, string Id, string Text, DateTimeOffset Timestamp, bool IsCatchUp = false, Attachment? Attachment = null);

public static class Topics
{
    public static byte[] ForPeer(PeerId id) =>
        SHA1.HashData(Encoding.UTF8.GetBytes("yaparr/peer/v1/" + id.Value));
}

internal sealed class Wire
{
    [JsonPropertyName("t")] public string Type { get; set; } = "";
    public string? Name { get; set; }
    public string[]? Rooms { get; set; }
    public string? Room { get; set; }
    public string? Id { get; set; }
    public string? Text { get; set; }
    public long Ts { get; set; }
    public WirePeer[]? Peers { get; set; }
    public StoredMessage[]? Msgs { get; set; }
    public string[]? Have { get; set; }
    public StoredPresence[]? Pres { get; set; }
    public StoredVoice[]? Vox { get; set; }
    public int Deg { get; set; }

    /// <summary>Whether the sender's connection is good enough to relay for the other members of a room. Absent from older versions.</summary>
    public bool? Rly { get; set; }

    /// <summary>Settings passed between a person's own devices.</summary>
    public AccountEntry[]? Entries { get; set; }

    /// <summary>Announcements of shared streams (a screen, a large voice channel), passed around the room.</summary>
    public StoredMedia[]? Ann { get; set; }

    /// <summary>Entries of a room's soundboard.</summary>
    public StoredSound[]? Snd { get; set; }

    /// <summary>Someone played a sound.</summary>
    public StoredPlay? Pl { get; set; }

    /// <summary>A control message between neighbours in a stream's tree.</summary>
    public WireMedia? Mx { get; set; }

    /// <summary>Direct messages passed between a person's own devices, and what they already have of them.</summary>
    public DirectRecord[]? Dm { get; set; }

    /// <summary>Set when <see cref="Dm"/> holds older messages being caught up on rather than a new one.</summary>
    public bool? Cu { get; set; }

    public const string Direct = "dm", DirectSync = "dmsync";

    public const string Caps = "caps", Rename = "name", Account = "acct", Media = "media", Sound = "snd", Play = "play";

    public const string Ack = "ack", Hello = "hi", Join = "join", Leave = "leave", Message = "msg", Introduce = "peers", Sync = "sync", History = "hist", Links = "links", Presence = "pres", Voice = "voice";
}

internal sealed class WirePeer
{
    public string Id { get; set; } = "";
    public string EndPoint { get; set; } = "";
    public string? Name { get; set; }
}
