using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Yaparr.Identity;

namespace Yaparr.Chat;

public static class ChannelIds
{
    public const byte Chat = 1;
    public const byte Voice = 16;
}

public sealed record PeerInfo(PeerId Id, string DisplayName, IPEndPoint? EndPoint, bool IsRelayed = false);

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

public sealed record ChatMessage(PeerInfo From, string? Room, string Id, string Text, DateTimeOffset Timestamp, bool IsCatchUp = false);

public static class Topics
{
    public static byte[] ForRoom(string roomName) =>
        SHA1.HashData(Encoding.UTF8.GetBytes("yaparr/room/v1/" + roomName.Trim().ToLowerInvariant()));

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
    public int Deg { get; set; }

    public const string Ack = "ack", Hello = "hi", Join = "join", Leave = "leave", Message = "msg", Introduce = "peers", Sync = "sync", History = "hist", Links = "links", Presence = "pres";
}

internal sealed class WirePeer
{
    public string Id { get; set; } = "";
    public string EndPoint { get; set; } = "";
    public string? Name { get; set; }
}
