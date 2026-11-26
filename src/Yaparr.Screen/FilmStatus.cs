using System.Buffers.Binary;
using System.Text;

namespace Yaparr.Screen;

/// <summary>
/// What a film that is played into a room says about itself, once a second and whenever it changes: what is playing, how far it is, how long it is and whether it is paused.
/// Viewers show it over the picture. It travels as a stream of its own that names the share (like the sound does, see <see cref="ScreenSharer.SoundKind"/>), so a viewer
/// that does not know of it (an older version) is not troubled, and a share that is no film has none.
/// </summary>
public readonly record struct FilmStatus(string Title, TimeSpan Position, TimeSpan Duration, bool Paused)
{
    public const string Kind = "film-status";

    /// <summary>The name of the status stream of the share with this id.</summary>
    public static string NameFor(string shareId) => "film:" + shareId;

    private const byte Version = 1;
    private const int MaxTitleBytes = 160;

    /// <summary>One byte of version, one of flags (the lowest bit: paused), the position and the length in milliseconds (8 bytes each), then the title as UTF-8.</summary>
    public byte[] Encode()
    {
        var title = Encoding.UTF8.GetBytes(Title);
        int length = title.Length;
        if (length > MaxTitleBytes)
        {
            // Cut on a character, not in the middle of one.
            length = MaxTitleBytes;
            while (length > 0 && (title[length] & 0xC0) == 0x80) length--;
        }
        var bytes = new byte[18 + length];
        bytes[0] = Version;
        bytes[1] = (byte)(Paused ? 1 : 0);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(2), (long)Position.TotalMilliseconds);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(10), (long)Duration.TotalMilliseconds);
        title.AsSpan(0, length).CopyTo(bytes.AsSpan(18));
        return bytes;
    }

    public static bool TryDecode(ReadOnlySpan<byte> bytes, out FilmStatus status)
    {
        status = default;
        // The one-byte packets a stream sends after its last frame (to show what is missing) are not statuses.
        if (bytes.Length < 18 || bytes[0] != Version) return false;
        long position = BinaryPrimitives.ReadInt64BigEndian(bytes[2..]);
        long duration = BinaryPrimitives.ReadInt64BigEndian(bytes[10..]);
        if (position < 0 || duration < 0 || position > TimeSpan.MaxValue.TotalMilliseconds || duration > TimeSpan.MaxValue.TotalMilliseconds) return false;
        status = new FilmStatus(Encoding.UTF8.GetString(bytes[18..]), TimeSpan.FromMilliseconds(position), TimeSpan.FromMilliseconds(duration), (bytes[1] & 1) != 0);
        return true;
    }
}
