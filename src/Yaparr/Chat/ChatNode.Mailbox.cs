using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Yaparr.Chat;

/// <summary>A message of a room that is sealed and ready to be left in the room's mailbox on a Yaparr Cloud server (see <c>docs/mailbox.md</c>).</summary>
/// <param name="MailboxId">Names the mailbox: derived from the room's secret, so only its members can compute it.</param>
/// <param name="ItemId">The message's own key (<see cref="ChatMessage.Key"/>): posting it again stores it once.</param>
/// <param name="Body">Base64 of the sealed message; the server cannot open it.</param>
public sealed record MailboxPost(string MailboxId, string ItemId, string Body);

/// <summary>
/// Seals a signed room message for its mailbox, and opens one. AES-256-GCM under a key made from the room's secret (<see cref="RoomKey.MailboxKey"/>), a random
/// nonce for each message, and the mailbox id as associated data so an item cannot be moved to another mailbox. What is opened is checked as any message is
/// (<see cref="SignedMessage.Verify"/>): the server, which holds only these, is not believed about anything.
/// </summary>
internal static class MailboxSeal
{
    private const int NonceSize = 12, TagSize = 16;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Seal(RoomKey room, StoredMessage message) => SealBytes(room.MailboxKey, room.MailboxId, JsonSerializer.SerializeToUtf8Bytes(message, Json));

    public static bool TryOpen(RoomKey room, string body, out StoredMessage message)
    {
        message = null!;
        if (!TryOpenBytes(room.MailboxKey, room.MailboxId, body, out var plain)) return false;
        try
        {
            message = JsonSerializer.Deserialize<StoredMessage>(plain, Json)!;
            return message != null && message.From != null && message.Pub != null && message.Name != null && message.Id != null && message.Text != null && message.Sig != null;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Seals bytes under <paramref name="key"/> for the mailbox <paramref name="mailboxId"/> (the associated data, so an item cannot be moved to another mailbox).</summary>
    public static string SealBytes(byte[] key, string mailboxId, byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag, System.Text.Encoding.ASCII.GetBytes(mailboxId));
        var sealedBytes = new byte[NonceSize + cipher.Length + TagSize];
        nonce.CopyTo(sealedBytes, 0);
        cipher.CopyTo(sealedBytes, NonceSize);
        tag.CopyTo(sealedBytes, NonceSize + cipher.Length);
        return Convert.ToBase64String(sealedBytes);
    }

    public static bool TryOpenBytes(byte[] key, string mailboxId, string body, out byte[] plain)
    {
        plain = Array.Empty<byte>();
        try
        {
            var all = Convert.FromBase64String(body);
            if (all.Length < NonceSize + TagSize + 1) return false;
            var cipherLength = all.Length - NonceSize - TagSize;
            var opened = new byte[cipherLength];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(all.AsSpan(0, NonceSize), all.AsSpan(NonceSize, cipherLength), all.AsSpan(NonceSize + cipherLength, TagSize), opened, System.Text.Encoding.ASCII.GetBytes(mailboxId));
            plain = opened;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { return false; }
    }
}

public sealed partial class ChatNode
{
    private const int MaxMailboxQueue = 500;

    /// <summary>Messages this device authored that are waiting to be left in a mailbox (only while <see cref="MailboxEnabled"/>).</summary>
    private readonly ConcurrentQueue<MailboxPost> _mailboxQueue = new();

    /// <summary>
    /// Whether the messages written here are also sealed and queued for the room's mailbox (see <see cref="TakeMailboxPosts"/>). The app turns it on while it is
    /// signed in to a Yaparr Cloud server, and turns it off otherwise, so nothing piles up that nobody will send.
    /// </summary>
    public bool MailboxEnabled { get; set; }

    /// <summary>A message was queued for a mailbox: the app sends it soon (a burst is one call).</summary>
    public event Action? MailboxQueued;

    /// <summary>A message that arrives from a mailbox this recent is announced like a live one (a notification); an older one is only shown, as a missed message.</summary>
    internal static readonly TimeSpan MailboxFreshWindow = TimeSpan.FromMinutes(10);

    private void QueueForMailbox(ChatRoom room, StoredMessage message)
    {
        if (!MailboxEnabled || _mailboxQueue.Count >= MaxMailboxQueue) return;
        var key = room.CurrentKey;
        _mailboxQueue.Enqueue(new MailboxPost(key.MailboxId, SignedMessage.Key(message), MailboxSeal.Seal(key, message)));
        MailboxQueued?.Invoke();
    }

    /// <summary>Takes up to <paramref name="max"/> sealed messages to leave in mailboxes, oldest first. Give back (<see cref="ReturnMailboxPosts"/>) any that could not be sent.</summary>
    public IReadOnlyList<MailboxPost> TakeMailboxPosts(int max)
    {
        var list = new List<MailboxPost>();
        while (list.Count < max && _mailboxQueue.TryDequeue(out var post)) list.Add(post);
        return list;
    }

    /// <summary>Puts messages back that could not be sent, to be tried again (those over the queue's limit are dropped).</summary>
    public void ReturnMailboxPosts(IEnumerable<MailboxPost> posts)
    {
        // Behind what was queued since, which is fine: the order in a mailbox does not matter, messages carry their own time.
        foreach (var post in posts)
            if (_mailboxQueue.Count < MaxMailboxQueue) _mailboxQueue.Enqueue(post);
    }

    /// <summary>The mailbox ids of the rooms this device is in now (a room whose secret was replaced has a new one), and of the direct conversations that have a secret.</summary>
    public IReadOnlyList<string> MailboxIds() => _rooms.Values.Select(r => r.CurrentKey.MailboxId).Concat(DirectMailboxIds()).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Opens what was fetched from a room's mailbox and hands it to the room like messages caught up from a member: checked, kept, shown, with the same blocks, bans
    /// and deletions. What does not open, or does not verify, is dropped. Returns how many were good (including any already held).
    /// </summary>
    public int ReceiveFromMailbox(string mailboxId, IReadOnlyList<string> bodies)
    {
        var room = _rooms.Values.FirstOrDefault(r => string.Equals(r.CurrentKey.MailboxId, mailboxId, StringComparison.Ordinal));
        if (room == null) return ReceiveDirectFromMailbox(mailboxId, bodies);
        var key = room.CurrentKey;
        var opened = new List<StoredMessage>();
        foreach (var body in bodies)
            if (MailboxSeal.TryOpen(key, body, out var message)) opened.Add(message);
            else _options.Log?.Invoke($"dropped an item from the mailbox of '{room.Name}': it does not open");
        if (opened.Count == 0) return 0;
        int before = 0;
        foreach (var batch in opened.Chunk(100))
        {
            var received = new List<string>();
            ProcessGroupMessages(null, room, batch, live: false, received, fromMailbox: true);
            before += received.Count;
        }
        return before;
    }
}
