using QRCoder;

namespace Yaparr.AppCore;

/// <summary>
/// The QR code that links a phone to an account: the link code and its passphrase together, because the code is only ever shown
/// on a screen to someone standing next to it. The code leaves out the room list (a code with many rooms would not fit in a QR
/// code): rooms follow from the other device once the new one is connected to it, as they do for any linked device.
/// </summary>
public static class LinkQr
{
    private const string CodePrefix = "YLNK1.";
    private const char Separator = '|';

    /// <summary>The text that goes into the QR code.</summary>
    public static string Payload(string code, string passphrase)
    {
        if (!code.StartsWith(CodePrefix, StringComparison.Ordinal)) throw new ArgumentException("That is not a link code.", nameof(code));
        return code + Separator + passphrase;
    }

    /// <summary>Takes the link code and the passphrase out of what a scan returned. False if it is anything else.</summary>
    public static bool TryParse(string? text, out string code, out string passphrase)
    {
        code = passphrase = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        int at = trimmed.IndexOf(Separator);
        if (at < 0 || !trimmed.StartsWith(CodePrefix, StringComparison.Ordinal)) return false;
        code = trimmed[..at];
        passphrase = trimmed[(at + 1)..].Trim();
        return code.Length > CodePrefix.Length && passphrase.Length > 0;
    }

    /// <summary>
    /// The squares of the QR code, true for a dark one, with the blank border the scanners need already around it.
    /// Throws <see cref="ArgumentException"/> if the text is too long for a QR code.
    /// </summary>
    public static bool[,] Matrix(string text)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.L);
            var rows = data.ModuleMatrix;
            int size = rows.Count;
            var matrix = new bool[size, size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    matrix[y, x] = rows[y][x];
            return matrix;
        }
        catch (QRCoder.Exceptions.DataTooLongException)
        {
            throw new ArgumentException("That is too much text for a QR code.", nameof(text));
        }
    }
}
