using System.Security.Cryptography;
using System.Text;
using Snip.Core;
using Xunit;

namespace Snip.Core.Tests;

public sealed class EncryptedConfigurationArchiveTests
{
    private const string Password = "Public 密码 1234";
    [Fact]
    public void PortableArchiveRoundTripsWithoutPlaintextSecretsAndUsesFreshRandomness()
    {
        var data = Encoding.UTF8.GetBytes("{\"ApiKey\":\"PUBLIC-ONLY-KEY-1234\",\"Provider\":\"公开测试配置\"}");
        var first = EncryptedConfigurationArchive.Encrypt(data, Password);
        var second = EncryptedConfigurationArchive.Encrypt(data, Password);
        Assert.False(first.SequenceEqual(second));
        Assert.Equal(data, EncryptedConfigurationArchive.Decrypt(first, Password));
        Assert.Equal(data, EncryptedConfigurationArchive.Decrypt(second, Password));
        Assert.DoesNotContain("PUBLIC-ONLY-KEY-1234", Encoding.UTF8.GetString(first));
        Assert.DoesNotContain("公开测试配置", Encoding.UTF8.GetString(first));
    }
    [Fact]
    public void WrongPasswordAndTamperedCiphertextNeverReturnPartialConfiguration()
    {
        var archive = EncryptedConfigurationArchive.Encrypt("PUBLIC SECRET"u8, Password);
        Assert.Throws<CryptographicException>(() => EncryptedConfigurationArchive.Decrypt(archive, "Wrong password"));
        foreach (int index in new[] { 8, 24, 36, archive.Length - 1 })
        {
            var altered = archive.ToArray(); altered[index] ^= 1;
            var error = Assert.Throws<CryptographicException>(() => EncryptedConfigurationArchive.Decrypt(altered, Password));
            Assert.DoesNotContain("PUBLIC", error.Message);
        }
    }
    [Fact]
    public void UntrustedSizeVersionAndTruncationAreRejectedBeforeDecryption()
    {
        foreach (int size in new[] { 0, 7, 36, 52, EncryptedConfigurationArchive.MaximumArchiveBytes + 1 })
            Assert.Throws<InvalidDataException>(() => EncryptedConfigurationArchive.Decrypt(new byte[size], Password));
        var unsupported = new byte[60]; "SNIPCFG2"u8.CopyTo(unsupported);
        Assert.Throws<InvalidDataException>(() => EncryptedConfigurationArchive.Decrypt(unsupported, Password));
        Assert.Throws<ArgumentException>(() => EncryptedConfigurationArchive.Encrypt([], Password));
        Assert.Throws<ArgumentException>(() => EncryptedConfigurationArchive.Encrypt(new byte[EncryptedConfigurationArchive.MaximumPlaintextBytes + 1], Password));
        Assert.Throws<ArgumentException>(() => EncryptedConfigurationArchive.Encrypt("text"u8, "short"));
    }
}
