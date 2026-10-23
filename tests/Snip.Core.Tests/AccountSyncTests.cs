using System.Net;
using System.Text;
using Snip.Core;
using Xunit;

namespace Snip.Core.Tests;

public sealed class AccountSyncTests
{
    [Fact]
    public void AccountKeysAreDomainSeparatedAndReproducibleOnAnotherDevice()
    {
        string salt = Convert.ToBase64String(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
        var work = AccountSyncProtocol.Derive("PUBLIC correct password 123", salt);
        var home = AccountSyncProtocol.Derive("PUBLIC correct password 123", salt);
        var wrong = AccountSyncProtocol.Derive("PUBLIC different password 123", salt);
        Assert.Equal(work.AuthSecret, home.AuthSecret); Assert.Equal(work.VaultPassword, home.VaultPassword);
        Assert.NotEqual(work.AuthSecret, work.VaultPassword); Assert.NotEqual(work.VaultPassword, wrong.VaultPassword);
        Assert.DoesNotContain(work.AuthSecret, work.ToString()); Assert.DoesNotContain(work.VaultPassword, work.ToString());
        var archive = EncryptedConfigurationArchive.Encrypt("PUBLIC API KEY"u8, work.VaultPassword);
        Assert.Equal("PUBLIC API KEY"u8.ToArray(), EncryptedConfigurationArchive.Decrypt(archive, home.VaultPassword));
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => EncryptedConfigurationArchive.Decrypt(archive, wrong.VaultPassword));
        Assert.Throws<ArgumentException>(() => AccountSyncProtocol.Derive("short", salt));
        Assert.Throws<ArgumentException>(() => AccountSyncProtocol.Derive("PUBLIC correct password 123", "invalid"));
    }
    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("https://user:password@example.com/")]
    [InlineData("https://example.com/?key=PRIVATE")]
    [InlineData("https://example.com/#PRIVATE")]
    public void ServerRejectsInsecureOrCredentialBearingAddresses(string value)
        => Assert.Throws<ArgumentException>(() => AccountSyncProtocol.Server(value));
    [Fact]
    public void ReverseProxyPrefixAndCanonicalAccountNameArePreserved()
    {
        Assert.Equal("https://example.com/selfsnip/", AccountSyncProtocol.Server("https://example.com/selfsnip").AbsoluteUri);
        Assert.Equal("public-owner", AccountSyncProtocol.UserName(" PUBLIC-OWNER "));
        Assert.Throws<ArgumentException>(() => AccountSyncProtocol.UserName("../escape"));
        Assert.Throws<ArgumentException>(() => AccountSyncProtocol.UserName("x"));
    }
    [Theory]
    [InlineData("https://bilibilil.online/selfsnip/health", "https://bilibilil.online/selfsnip/")]
    [InlineData("https://example.com/prefix/health/", "https://example.com/prefix/")]
    [InlineData("http://127.0.0.1:5080/health", "http://127.0.0.1:5080/")]
    [InlineData("https://example.com/prefix/", "https://example.com/prefix/")]
    public void HealthAddressIsNormalizedWithoutChangingTheServicePrefix(string input, string expected)
        => Assert.Equal(expected, AccountSyncProtocol.Server(input).AbsoluteUri);
    [Fact]
    public async Task ClientDoesNotFollowRedirectsOrDiscloseServerErrorBodies()
    {
        foreach (var code in new[] { 302, 401, 409, 429, 503 })
        {
            int calls = 0;
            using var client = new HttpClient(new Handler((_, _) =>
            {
                calls++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("PRIVATE-KEY PRIVATE-SESSION PRIVATE-BODY") });
            }));
            var error = await Assert.ThrowsAsync<AccountSyncException>(() => new AccountSyncApi(new("https://example.com/selfsnip/"), client).GetAsync("PUBLIC-SESSION", null, default));
            Assert.Equal(1, calls); Assert.DoesNotContain("PRIVATE", error.Message);
            Assert.Equal(code == 401, error.Unauthorized); Assert.Equal(code == 409, error.Conflict);
        }
    }
    [Fact]
    public async Task ClientKeepsBearerOutOfUrlsAndRejectsOversizedVaultBeforeUse()
    {
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("https://example.com/selfsnip/api/v1/vault", request.RequestUri!.AbsoluteUri);
            Assert.Equal("PUBLIC-SESSION", request.Headers.Authorization!.Parameter);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[EncryptedConfigurationArchive.MaximumArchiveBytes + 1]) };
            response.Headers.ETag = new("\"1\""); return Task.FromResult(response);
        }));
        await Assert.ThrowsAsync<AccountSyncException>(() => new AccountSyncApi(new("https://example.com/selfsnip/"), client).GetAsync("PUBLIC-SESSION", null, default));
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
}
