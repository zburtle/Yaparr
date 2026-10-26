using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Yaparr.Transport;

internal sealed class SessionCrypto : IDisposable
{
    public const int CounterLength = 8;
    public const int TagLength = 16;

    private readonly AesGcm _send;
    private readonly AesGcm _recv;
    private ulong _sendCounter;
    private ulong _recvMax;
    private ulong _recvBitmap;

    public SessionCrypto(byte[] sendKey, byte[] recvKey)
    {
        _send = new AesGcm(sendKey, TagLength);
        _recv = new AesGcm(recvKey, TagLength);
    }

    public byte[] Encrypt(ReadOnlySpan<byte> header, ReadOnlySpan<byte> plaintext)
    {
        ulong counter = ++_sendCounter;
        var packet = new byte[header.Length + CounterLength + plaintext.Length + TagLength];
        header.CopyTo(packet);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(header.Length), counter);

        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
        int bodyStart = header.Length + CounterLength;
        _send.Encrypt(nonce, plaintext,
            packet.AsSpan(bodyStart, plaintext.Length),
            packet.AsSpan(bodyStart + plaintext.Length, TagLength),
            packet.AsSpan(0, bodyStart));
        return packet;
    }

    public byte[]? Decrypt(ReadOnlySpan<byte> packet, int headerLength)
    {
        int bodyStart = headerLength + CounterLength;
        if (packet.Length < bodyStart + TagLength) return null;

        ulong counter = BinaryPrimitives.ReadUInt64BigEndian(packet[headerLength..]);
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);

        int len = packet.Length - bodyStart - TagLength;
        var plaintext = new byte[len];
        try
        {
            _recv.Decrypt(nonce, packet.Slice(bodyStart, len), packet.Slice(bodyStart + len, TagLength), plaintext, packet[..bodyStart]);
        }
        catch (CryptographicException) { return null; }

        return AcceptCounter(counter) ? plaintext : null;
    }

    private bool AcceptCounter(ulong c)
    {
        if (c == 0) return false;
        if (c > _recvMax)
        {
            ulong shift = c - _recvMax;
            _recvBitmap = shift >= 64 ? 0 : _recvBitmap << (int)shift;
            _recvBitmap |= 1;
            _recvMax = c;
            return true;
        }
        ulong diff = _recvMax - c;
        if (diff >= 64) return false;
        ulong bit = 1UL << (int)diff;
        if ((_recvBitmap & bit) != 0) return false;
        _recvBitmap |= bit;
        return true;
    }

    public void Dispose()
    {
        _send.Dispose();
        _recv.Dispose();
    }
}
