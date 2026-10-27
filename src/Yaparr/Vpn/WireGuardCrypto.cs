using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace Yaparr.Vpn;

internal static class WireGuardCrypto
{
    public const int KeyLength = 32;
    public const int HashLength = 32;
    public const int TagLength = 16;

    public static byte[] Hash(params byte[][] parts)
    {
        var d = new Blake2sDigest(256);
        foreach (var p in parts) d.BlockUpdate(p, 0, p.Length);
        var output = new byte[HashLength];
        d.DoFinal(output, 0);
        return output;
    }

    public static byte[] Mac16(byte[] key, byte[] data)
    {
        var d = new Blake2sDigest(key, 16, null, null);
        d.BlockUpdate(data, 0, data.Length);
        var output = new byte[16];
        d.DoFinal(output, 0);
        return output;
    }

    public static byte[] Hmac(byte[] key, params byte[][] parts)
    {
        const int block = 64;
        if (key.Length > block) key = Hash(key);
        var ipad = new byte[block];
        var opad = new byte[block];
        for (int i = 0; i < block; i++)
        {
            byte k = i < key.Length ? key[i] : (byte)0;
            ipad[i] = (byte)(k ^ 0x36);
            opad[i] = (byte)(k ^ 0x5c);
        }
        var inner = Hash(new[] { ipad }.Concat(parts).ToArray());
        return Hash(opad, inner);
    }

    public static byte[] Kdf1(byte[] key, byte[] input)
    {
        var t0 = Hmac(key, input);
        return Hmac(t0, new byte[] { 1 });
    }

    public static (byte[], byte[]) Kdf2(byte[] key, byte[] input)
    {
        var t0 = Hmac(key, input);
        var t1 = Hmac(t0, new byte[] { 1 });
        var t2 = Hmac(t0, t1, new byte[] { 2 });
        return (t1, t2);
    }

    public static (byte[], byte[], byte[]) Kdf3(byte[] key, byte[] input)
    {
        var t0 = Hmac(key, input);
        var t1 = Hmac(t0, new byte[] { 1 });
        var t2 = Hmac(t0, t1, new byte[] { 2 });
        var t3 = Hmac(t0, t2, new byte[] { 3 });
        return (t1, t2, t3);
    }

    public static byte[] PublicKey(byte[] privateKey)
    {
        var pub = new byte[KeyLength];
        X25519.ScalarMultBase(privateKey, 0, pub, 0);
        return pub;
    }

    public static byte[] GeneratePrivateKey()
    {
        var key = RandomNumberGenerator.GetBytes(KeyLength);
        key[0] &= 248;
        key[31] &= 127;
        key[31] |= 64;
        return key;
    }

    public static byte[] Dh(byte[] privateKey, byte[] publicKey)
    {
        var shared = new byte[KeyLength];
        if (!X25519.CalculateAgreement(privateKey, 0, publicKey, 0, shared, 0))
            throw new CryptographicException("Invalid public key.");
        return shared;
    }

    private static byte[] Nonce(ulong counter)
    {
        var nonce = new byte[12];
        BitConverter.TryWriteBytes(nonce.AsSpan(4), counter);
        if (!BitConverter.IsLittleEndian) Array.Reverse(nonce, 4, 8);
        return nonce;
    }

    public static byte[] Seal(byte[] key, ulong counter, byte[] plaintext, byte[] aad)
    {
        var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        cipher.Init(true, new AeadParameters(new KeyParameter(key), TagLength * 8, Nonce(counter), aad));
        var output = new byte[cipher.GetOutputSize(plaintext.Length)];
        int n = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
        cipher.DoFinal(output, n);
        return output;
    }

    public static byte[]? Open(byte[] key, ulong counter, ReadOnlySpan<byte> ciphertext, byte[] aad)
    {
        try
        {
            var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
            cipher.Init(false, new AeadParameters(new KeyParameter(key), TagLength * 8, Nonce(counter), aad));
            var input = ciphertext.ToArray();
            var output = new byte[cipher.GetOutputSize(input.Length)];
            int n = cipher.ProcessBytes(input, 0, input.Length, output, 0);
            cipher.DoFinal(output, n);
            return output;
        }
        catch (Org.BouncyCastle.Crypto.InvalidCipherTextException)
        {
            return null;
        }
    }

    public static byte[] Tai64n(DateTimeOffset now)
    {
        var b = new byte[12];
        ulong seconds = 0x400000000000000aUL + (ulong)now.ToUnixTimeSeconds();
        for (int i = 0; i < 8; i++) b[i] = (byte)(seconds >> (56 - 8 * i));
        uint nanos = (uint)((now.Ticks % TimeSpan.TicksPerSecond) * 100);
        for (int i = 0; i < 4; i++) b[8 + i] = (byte)(nanos >> (24 - 8 * i));
        return b;
    }

    public static readonly byte[] Construction = Encoding.ASCII.GetBytes("Noise_IKpsk2_25519_ChaChaPoly_BLAKE2s");
    public static readonly byte[] Identifier = Encoding.ASCII.GetBytes("WireGuard v1 zx2c4 Jason@zx2c4.com");
    public static readonly byte[] LabelMac1 = Encoding.ASCII.GetBytes("mac1----");
}
