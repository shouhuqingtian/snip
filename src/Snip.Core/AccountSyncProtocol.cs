using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Snip.Core;

public static partial class AccountSyncProtocol
{
    public const int Version = 1;
    public const string DefaultServer = "https://bilibilil.online/selfsnip/";
    public static string UserName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("请填写同步账号。");
        var name = value.Trim().ToLowerInvariant();
        if (!NamePattern().IsMatch(name)) throw new ArgumentException("账号须为 3–64 位英文字母、数字、点、下划线或短横线。");
        return name;
    }
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
    public static Uri Server(string value)
    {
        if (value is null || value.Length > 2048 || !Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var server) ||
            !(server.Scheme == "https" || server.IsLoopback && server.Scheme == "http") || server.UserInfo.Length > 0 || server.Query.Length > 0 || server.Fragment.Length > 0)
            throw new ArgumentException("同步服务器须填写 HTTPS 地址；仅本机测试可使用 HTTP。不要在地址中填写密码或令牌。");
        // A copied health-check URL identifies the same service root, not an API prefix.
        string path = server.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/health", StringComparison.OrdinalIgnoreCase))
            server = new UriBuilder(server) { Path = path[..^"health".Length] }.Uri;
        return server;
    }
    public static bool ValidSalt(string salt) => Base64Bytes(salt, 16);
    public static bool ValidAuthSecret(string secret) => Base64Bytes(secret, 32);
    private static bool Base64Bytes(string value, int count)
    {
        if (value is null || value.Length > 128) return false;
        Span<byte> bytes = stackalloc byte[64];
        return Convert.TryFromBase64String(value, bytes, out int written) && written == count;
    }
    public static AccountSyncSecrets Derive(string password, string salt)
    {
        if (password.Length is < 12 or > 256) throw new ArgumentException("账号密码须为 12–256 个字符；它也用于解密同步配置，请妥善保存。");
        if (!ValidSalt(salt)) throw new ArgumentException("同步服务器的账号参数无效。");
        byte[] master = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(salt), 600_000, HashAlgorithmName.SHA256, 32);
        byte[]? auth = null, vault = null;
        try
        {
            auth = HMACSHA256.HashData(master, "SelfSnip.AccountAuth.v1"u8);
            vault = HMACSHA256.HashData(master, "SelfSnip.ConfigurationVault.v1"u8);
            return new(Convert.ToBase64String(auth), Convert.ToBase64String(vault));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
            if (auth is not null) CryptographicOperations.ZeroMemory(auth);
            if (vault is not null) CryptographicOperations.ZeroMemory(vault);
        }
    }
}

public sealed class AccountSyncSecrets(string authSecret, string vaultPassword)
{
    public string AuthSecret { get; } = authSecret;
    public string VaultPassword { get; } = vaultPassword;
    public override string ToString() => "Account sync secrets (redacted)";
}
public sealed class SyncRegisterRequest
{
    public string UserName { get; set; } = "";
    public string Salt { get; set; } = "";
    public string AuthSecret { get; set; } = "";
    public override string ToString() => "Registration (redacted)";
}
public sealed class SyncLoginRequest
{
    public string UserName { get; set; } = "";
    public string AuthSecret { get; set; } = "";
    public override string ToString() => "Login (redacted)";
}
public sealed record SyncAccountParameters(int Version, string Salt);
public sealed class SyncLoginResponse
{
    public string Token { get; set; } = "";
    public DateTimeOffset ExpiresUtc { get; set; }
    public override string ToString() => "Session (redacted)";
}
public sealed record SyncUserNameRequest(string UserName);
public sealed record SyncRemoteVault(long Revision, byte[]? Archive, bool Unchanged = false);
public sealed class AccountSyncException(string message, bool unauthorized = false, bool conflict = false, int statusCode = 0) : InvalidOperationException(message)
{
    public bool Unauthorized { get; } = unauthorized;
    public bool Conflict { get; } = conflict;
    public int StatusCode { get; } = statusCode;
}
