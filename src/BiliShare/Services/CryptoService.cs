using System.Security.Cryptography;

namespace BiliShare.Services;

/// <summary>
/// AES-256-GCM 加解密：密钥 32 字节存 <c>data/key.dat</c>，密文格式为 nonce(12) + 密文 + tag(16)。
/// </summary>
public static class CryptoService
{
    private const int KeySizeBytes = 32;   // 256 位
    private const int NonceSizeBytes = 12; // GCM 推荐 96 位
    private const int TagSizeBytes = 16;   // 128 位

    /// <summary>生成随机 32 字节密钥。</summary>
    public static byte[] GenerateKey() => RandomNumberGenerator.GetBytes(KeySizeBytes);

    /// <summary>加载本地密钥；不存在则生成并持久化到 key.dat。</summary>
    public static byte[] LoadOrCreateKey()
    {
        if (File.Exists(Paths.LocalKeyFile))
            return File.ReadAllBytes(Paths.LocalKeyFile);

        var key = GenerateKey();
        Paths.EnsureDirectories();
        File.WriteAllBytes(Paths.LocalKeyFile, key);
        return key;
    }

    /// <summary>加密，返回 nonce + ciphertext + tag 的拼接。</summary>
    public static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];

        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[NonceSizeBytes + ciphertext.Length + TagSizeBytes];
        nonce.CopyTo(result, 0);
        ciphertext.CopyTo(result, NonceSizeBytes);
        tag.CopyTo(result, NonceSizeBytes + ciphertext.Length);
        return result;
    }

    /// <summary>解密 nonce + ciphertext + tag 的拼接；tag 不匹配抛 <see cref="CryptographicException"/>。</summary>
    public static byte[] Decrypt(byte[] encrypted, byte[] key)
    {
        if (encrypted.Length < NonceSizeBytes + TagSizeBytes)
            throw new CryptographicException("密文长度非法");

        var nonce = encrypted[..NonceSizeBytes];
        var cipherLen = encrypted.Length - NonceSizeBytes - TagSizeBytes;
        var ciphertext = encrypted[NonceSizeBytes..(NonceSizeBytes + cipherLen)];
        var tag = encrypted[(NonceSizeBytes + cipherLen)..];

        var plaintext = new byte[cipherLen];
        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}