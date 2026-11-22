namespace Yaparr.AppCore;

/// <summary>
/// Where the explanations live. A setting says what it does in a line, and under it a "Learn more about …" link opens the guide for it on the website
/// (yaparr.com/learn, made from <c>docs/guides</c> by the website repository's <c>scripts/sync-learn.mjs</c>). Apps ask for a topic by name, so a guide can move without touching
/// the windows: the topics are checked against the guides by a test.
/// </summary>
public static class Guides
{
    /// <summary>Where the guides are read: a page of the website for each guide file, and an anchor for each heading.</summary>
    public const string Base = "https://yaparr.com/learn/";

    // Topic: guide file (its page on the website has the name without .md) and, after the #, the heading it is about (as GitHub makes an anchor of it, which the website keeps).
    internal static readonly IReadOnlyDictionary<string, string> Topics = new Dictionary<string, string>
    {
        ["cloud"] = "yaparr-cloud.md#what-yaparr-cloud-is",
        ["cloud-sync"] = "yaparr-cloud.md#syncing",
        ["cloud-away"] = "yaparr-cloud.md#messages-for-people-who-are-away",
        ["cloud-gateway"] = "yaparr-cloud.md#when-your-network-blocks-udp",
        ["cloud-push"] = "yaparr-cloud.md#push-notifications",
        ["sync-key"] = "yaparr-cloud.md#the-sync-key",
        ["notifications"] = "notifications.md#notifications",
        ["background"] = "notifications.md#staying-connected-in-the-background",
        ["link-previews"] = "link-previews.md",
        ["updates"] = "updates.md",
        ["backups"] = "accounts.md#backups",
        ["accounts"] = "accounts.md#starting-on-a-new-device",
        ["logout"] = "accounts.md#logging-out",
        ["room-key"] = "rooms.md#replacing-a-rooms-key",
        ["gif-shelf"] = "rooms.md#the-gif-shelf",
        ["soundboard"] = "rooms.md#the-soundboard",
        ["networks"] = "networks.md",
        ["vpn"] = "networks.md#wireguard-vpn",
        ["turn"] = "networks.md#turn-relay",
        ["screen-share"] = "screen-sharing.md#sharing-a-window-or-a-screen",
        ["screen-formats"] = "screen-sharing.md#picture-formats",
        ["screen-quality"] = "screen-sharing.md#quality-that-adjusts",
        ["screen-sound"] = "screen-sharing.md#sound",
        ["voice"] = "voice.md",
    };

    /// <summary>The address of the guide for a topic, or null for a name that is not one.</summary>
    public static Uri? Url(string? topic) => topic != null && Topics.TryGetValue(topic, out var path) ? new Uri(Base + path.Replace(".md", "")) : null;
}
