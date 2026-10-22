using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Snip.Core;

namespace Snip.Sync.Server;

/// <summary>A single-server, atomic-file store for a private deployment. No original passwords, API keys or raw session tokens are persisted.</summary>
public sealed class AccountStore : IDisposable
{
    private readonly string directory, accountsPath, invite;
    private readonly FileStream lease;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly PasswordHasher<string> hasher = new();
    private readonly List<Account> accounts;
    private sealed class Account
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Salt { get; set; } = "";
        public string AuthHash { get; set; } = "";
        public int Failures { get; set; }
        public DateTimeOffset LockedUntil { get; set; }
        public List<Session> Sessions { get; set; } = [];
        public override string ToString() => "Account (redacted)";
    }
    private sealed record Session(string Hash, DateTimeOffset ExpiresUtc);
    public AccountStore(IConfiguration configuration)
    {
        invite = configuration["SELF_SNIP_REGISTER_INVITE"] ?? "";
        if (invite.Length is < 24 or > 256) throw new InvalidOperationException("Configure a registration invite of 24–256 characters.");
        directory = Path.GetFullPath(configuration["SELF_SNIP_DATA"] ?? "sync-data");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        lease = new FileStream(Path.Combine(directory, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        accountsPath = Path.Combine(directory, "accounts.json");
        if (File.Exists(accountsPath))
        {
            if (new FileInfo(accountsPath).Length > 1024 * 1024) throw new InvalidDataException("Account database exceeds its limit.");
            accounts = JsonSerializer.Deserialize<List<Account>>(File.ReadAllBytes(accountsPath)) ?? throw new InvalidDataException("Account database is invalid.");
            if (accounts.Count > 100 || accounts.Any(a => a is null || !Guid.TryParseExact(a.Id, "N", out _) || AccountSyncProtocol.UserName(a.Name) != a.Name ||
                !AccountSyncProtocol.ValidSalt(a.Salt) || string.IsNullOrEmpty(a.AuthHash) || a.AuthHash.Length > 2048 || a.Sessions is null || a.Sessions.Count > 8 ||
                a.Sessions.Any(s => s is null || s.Hash.Length != 64)) || accounts.Select(a => a.Name).Distinct().Count() != accounts.Count || accounts.Select(a => a.Id).Distinct().Count() != accounts.Count)
                throw new InvalidDataException("Account database is invalid.");
        }
        else accounts = [];
    }
    public async Task<string?> ParametersAsync(string name, CancellationToken token)
    {
        name = AccountSyncProtocol.UserName(name);
        await gate.WaitAsync(token);
        try { return accounts.FirstOrDefault(a => a.Name == name)?.Salt; }
        finally { gate.Release(); }
    }
    public async Task<(int Code, SyncLoginResponse? Response)> RegisterAsync(SyncRegisterRequest request, CancellationToken token)
    {
        string name = AccountSyncProtocol.UserName(request.UserName);
        if (!AccountSyncProtocol.ValidSalt(request.Salt) || !AccountSyncProtocol.ValidAuthSecret(request.AuthSecret) || request.InviteCode is null || request.InviteCode.Length > 256) return (400, null);
        if (!FixedEqual(request.InviteCode, invite)) return (403, null);
        await gate.WaitAsync(token);
        try
        {
            if (accounts.Any(a => a.Name == name)) return (409, null);
            if (accounts.Count >= 100) return (403, null);
            var account = new Account { Id = Guid.NewGuid().ToString("N"), Name = name, Salt = request.Salt, AuthHash = hasher.HashPassword(name, request.AuthSecret) };
            var response = Issue(account); accounts.Add(account);
            try { await SaveAsync(token); }
            catch { accounts.Remove(account); throw; }
            return (200, response);
        }
        finally { gate.Release(); }
    }
    public async Task<(int Code, SyncLoginResponse? Response)> LoginAsync(SyncLoginRequest request, CancellationToken token)
    {
        string name = AccountSyncProtocol.UserName(request.UserName);
        if (!AccountSyncProtocol.ValidAuthSecret(request.AuthSecret)) return (401, null);
        await gate.WaitAsync(token);
        try
        {
            var account = accounts.FirstOrDefault(a => a.Name == name); if (account is null) return (401, null);
            if (account.LockedUntil > DateTimeOffset.UtcNow) return (429, null);
            int previousFailures = account.Failures; var previousLock = account.LockedUntil; var previousSessions = account.Sessions.ToList();
            try
            {
            if (hasher.VerifyHashedPassword(name, account.AuthHash, request.AuthSecret) == PasswordVerificationResult.Failed)
            {
                account.Failures++;
                if (account.Failures >= 5) { account.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(15); account.Failures = 0; }
                await SaveAsync(token); return (401, null);
            }
            account.Failures = 0; account.LockedUntil = default;
            var result = Issue(account); await SaveAsync(token); return (200, result);
            }
            catch { account.Failures = previousFailures; account.LockedUntil = previousLock; account.Sessions = previousSessions; throw; }
        }
        finally { gate.Release(); }
    }
    public async Task<string?> AuthorizeAsync(string bearer, CancellationToken token)
    {
        if (bearer.Length is < 32 or > 256) return null;
        string hash = Hash(bearer); await gate.WaitAsync(token);
        try { return accounts.FirstOrDefault(a => a.Sessions.Any(s => s.ExpiresUtc > DateTimeOffset.UtcNow && FixedEqual(s.Hash, hash)))?.Id; }
        finally { gate.Release(); }
    }
    public async Task LogoutAsync(string bearer, CancellationToken token)
    {
        if (bearer.Length is < 32 or > 256) return;
        string hash = Hash(bearer); await gate.WaitAsync(token);
        try
        {
            bool changed = false; var previous = accounts.ToDictionary(a => a.Id, a => a.Sessions.ToList());
            foreach (var account in accounts) changed |= account.Sessions.RemoveAll(s => FixedEqual(s.Hash, hash) || s.ExpiresUtc <= DateTimeOffset.UtcNow) > 0;
            try { if (changed) await SaveAsync(token); }
            catch { foreach (var account in accounts) account.Sessions = previous[account.Id]; throw; }
        }
        finally { gate.Release(); }
    }
    public async Task<(long Revision, byte[] Archive)?> ReadVaultAsync(string user, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { return await LoadVaultAsync(user, token); }
        finally { gate.Release(); }
    }
    public async Task<long?> WriteVaultAsync(string user, long revision, byte[] archive, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var current = await LoadVaultAsync(user, token);
            if ((current?.Revision ?? 0) != revision || revision == long.MaxValue) return null;
            long next = revision + 1; var bytes = new byte[8 + archive.Length];
            BinaryPrimitives.WriteInt64BigEndian(bytes, next); archive.CopyTo(bytes, 8);
            await AtomicWriteAsync(VaultPath(user), bytes, token); return next;
        }
        finally { gate.Release(); }
    }
    private string VaultPath(string user)
    {
        if (!Guid.TryParseExact(user, "N", out _)) throw new InvalidDataException();
        return Path.Combine(directory, user + ".vault");
    }
    private async Task<(long Revision, byte[] Archive)?> LoadVaultAsync(string user, CancellationToken token)
    {
        var path = VaultPath(user); if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length is < 61 or > EncryptedConfigurationArchive.MaximumArchiveBytes + 8) throw new InvalidDataException();
        byte[] bytes = await File.ReadAllBytesAsync(path, token); long revision = BinaryPrimitives.ReadInt64BigEndian(bytes);
        if (revision <= 0 || !bytes.AsSpan(8, 8).SequenceEqual("SNIPCFG1"u8)) throw new InvalidDataException();
        return (revision, bytes[8..]);
    }
    private SyncLoginResponse Issue(Account account)
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); var expiry = DateTimeOffset.UtcNow.AddDays(30);
        account.Sessions.RemoveAll(s => s.ExpiresUtc <= DateTimeOffset.UtcNow);
        while (account.Sessions.Count >= 8) account.Sessions.RemoveAt(0);
        account.Sessions.Add(new(Hash(token), expiry)); return new() { Token = token, ExpiresUtc = expiry };
    }
    private Task SaveAsync(CancellationToken token) => AtomicWriteAsync(accountsPath, JsonSerializer.SerializeToUtf8Bytes(accounts), token);
    private static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken token)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await file.WriteAsync(bytes, token); await file.FlushAsync(token); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool FixedEqual(string first, string second)
    {
        byte[] a = SHA256.HashData(Encoding.UTF8.GetBytes(first)), b = SHA256.HashData(Encoding.UTF8.GetBytes(second));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
    public void Dispose() { lease.Dispose(); gate.Dispose(); }
}
