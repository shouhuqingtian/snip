using System.Security.Cryptography;

namespace Snip.Core;

/// <summary>Password-encrypted, portable settings. No DPAPI or machine identity is part of the format.</summary>
public static class EncryptedConfigurationArchive
{
    private static ReadOnlySpan<byte> Magic => "SNIPCFG1"u8;
    private const int SaltLength = 16, NonceLength = 12, TagLength = 16;
    private const int HeaderLength = 8 + SaltLength + NonceLength;
    private const int Iterations = 600_000;
    public const int MaximumPlaintextBytes = 512 * 1024;
    public const int MaximumArchiveBytes = HeaderLength + TagLength + MaximumPlaintextBytes;

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, string password)
    {
        ValidatePassword(password);
        if (plaintext.IsEmpty || plaintext.Length > MaximumPlaintextBytes)
            throw new ArgumentException("配置内容为空或超过大小上限。");
        var archive = new byte[HeaderLength + TagLength + plaintext.Length];
        Magic.CopyTo(archive);
        var salt = archive.AsSpan(Magic.Length, SaltLength);
        var nonce = archive.AsSpan(Magic.Length + SaltLength, NonceLength);
        RandomNumberGenerator.Fill(salt); RandomNumberGenerator.Fill(nonce);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using var cipher = new AesGcm(key, TagLength);
            cipher.Encrypt(nonce, plaintext, archive.AsSpan(HeaderLength + TagLength), archive.AsSpan(HeaderLength, TagLength), archive.AsSpan(0, HeaderLength));
            return archive;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> archive, string password)
    {
        ValidatePassword(password);
        if (archive.Length <= HeaderLength + TagLength || archive.Length > MaximumArchiveBytes || !archive[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("不是受支持的加密配置文件，或文件已损坏。");
        var salt = archive.Slice(Magic.Length, SaltLength);
        var nonce = archive.Slice(Magic.Length + SaltLength, NonceLength);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var plaintext = new byte[archive.Length - HeaderLength - TagLength];
        try
        {
            using var cipher = new AesGcm(key, TagLength);
            cipher.Decrypt(nonce, archive[(HeaderLength + TagLength)..], archive.Slice(HeaderLength, TagLength), plaintext, archive[..HeaderLength]);
            return plaintext;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new CryptographicException("配置密码不正确，或文件已损坏。");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static void ValidatePassword(string password)
    {
        if (password is null || password.Length is < 8 or > 256)
            throw new ArgumentException("配置密码须为 8–256 个字符。");
    }
}
