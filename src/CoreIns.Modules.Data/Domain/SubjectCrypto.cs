using System.Security.Cryptography;
using System.Text;

namespace CoreIns.Modules.Data.Domain;

/// <summary>AES-256-GCM sealing of subject facts under the per-subject key (crypto-shred, DAT-CORE).</summary>
internal static class SubjectCrypto
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    public static byte[] Seal(byte[] key, string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[data.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, data, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }

    public static string Open(byte[] key, byte[] sealedData)
    {
        var nonce = sealedData.AsSpan(0, NonceSize);
        var tag = sealedData.AsSpan(NonceSize, TagSize);
        var cipher = sealedData.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
