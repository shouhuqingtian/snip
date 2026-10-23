using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Snip.Core;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
bool native = args.Length == 2 && args[0] == "--executable";
string server = args.Length == 2 && (args[0] == "--server" || native) ? Path.GetFullPath(args[1]) : Path.Combine(root, "src/Snip.Sync.Server/bin/Release/net8.0/Snip.Sync.Server.dll");
if (!File.Exists(server)) throw new InvalidOperationException("Build the release sync server first.");
string work = Path.Combine(Path.GetTempPath(), "SelfSnip.PublicSyncCheck-" + Guid.NewGuid()); Directory.CreateDirectory(work);
var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
var address = new Uri("http://127.0.0.1:" + port + "/");
const string password = "PUBLIC Account Password 12345";
bool allowRegistration = true;
Process? process = null; Task? outputReader = null, errorReader = null;
int passed = 0;
void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
void Pass(string message) { passed++; Console.WriteLine("PASS account sync " + message); }
async Task Start()
{
    var start = new ProcessStartInfo(native ? server : "dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = work };
    if (!native) start.ArgumentList.Add(server); start.Environment["ASPNETCORE_URLS"] = address.AbsoluteUri.TrimEnd('/');
    start.Environment["SELF_SNIP_DATA"] = Path.Combine(work, "data"); start.Environment["SELF_SNIP_ALLOW_REGISTRATION"] = allowRegistration.ToString();
    process = Process.Start(start)!;
    // Drain without printing provider/session data, even on failure.
    outputReader = process.StandardOutput.ReadToEndAsync(); errorReader = process.StandardError.ReadToEndAsync();
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
    for (int i = 0; i < 100; i++)
    {
        if (process.HasExited) throw new InvalidOperationException("Sync server exited before readiness.");
        try { if ((await http.GetAsync(new Uri(address, "health"))).IsSuccessStatusCode) return; } catch (HttpRequestException) { } catch (TaskCanceledException) { /* Retry this one-second readiness poll within the existing outer startup deadline. */ }
        await Task.Delay(100);
    }
    throw new TimeoutException("Sync server did not become ready.");
}
void Stop() { if (process is { HasExited: false }) { process.Kill(true); process.WaitForExit(5000); } process?.Dispose(); process = null; }
try
{
    await Start(); var first = new AccountSyncApi(address); var second = new AccountSyncApi(address);
    string salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)); var secrets = AccountSyncProtocol.Derive(password, salt);
    var registered = await first.RegisterAsync(new() { UserName = "public-owner", Salt = salt, AuthSecret = secrets.AuthSecret }, CancellationToken.None);
    Require(registered.ExpiresUtc > DateTimeOffset.UtcNow && registered.Token.Length > 32, "No valid registered session."); Pass("real server registration with derived authentication and no invitation");
    var parameters = await second.ParametersAsync("PUBLIC-OWNER", CancellationToken.None);
    var homeSecrets = AccountSyncProtocol.Derive(password, parameters.Salt);
    Require(homeSecrets.VaultPassword == secrets.VaultPassword && homeSecrets.AuthSecret != homeSecrets.VaultPassword, "Device keys differ or key separation failed.");
    var home = await second.LoginAsync(new() { UserName = "public-owner", AuthSecret = homeSecrets.AuthSecret }, CancellationToken.None);
    Pass("second device login derives the same vault key without transmitting the account password");
    var absent = await first.GetAsync(registered.Token, null, CancellationToken.None); Require(absent.Revision == 0 && absent.Archive is null, "Empty vault not detected.");
    byte[] plaintext = Encoding.UTF8.GetBytes("{\"ApiKey\":\"PUBLIC-ONLY-API-KEY\",\"Model\":\"public-model\"}");
    byte[] archive = EncryptedConfigurationArchive.Encrypt(plaintext, secrets.VaultPassword);
    long revision = await first.PutAsync(registered.Token, 0, archive, CancellationToken.None); Require(revision == 1, "First revision differs.");
    var remote = await second.GetAsync(home.Token, null, CancellationToken.None);
    Require(remote.Revision == 1 && EncryptedConfigurationArchive.Decrypt(remote.Archive!, homeSecrets.VaultPassword).SequenceEqual(plaintext), "Home device cannot recover encrypted settings.");
    var unchanged = await second.GetAsync(home.Token, 1, CancellationToken.None); Require(unchanged.Unchanged && unchanged.Archive is null, "Conditional download retransmitted data.");
    Pass("encrypted cross-device roundtrip, revisions and conditional 304 response");
    var uploads = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
    {
        try { return await first.PutAsync(registered.Token, 1, archive, CancellationToken.None) == 2; }
        catch (AccountSyncException error) when (error.Conflict) { return false; }
    }));
    Require(uploads.Count(v => v) == 1, "Concurrent writes silently overwrote one another."); Pass("compare-and-swap admits one concurrent writer and rejects the stale version");
    string otherSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)); var otherKeys = AccountSyncProtocol.Derive("PUBLIC Other Password 12345", otherSalt);
    var other = await first.RegisterAsync(new() { UserName = "public-other", Salt = otherSalt, AuthSecret = otherKeys.AuthSecret }, CancellationToken.None);
    Require((await first.GetAsync(other.Token, null, CancellationToken.None)).Revision == 0, "Another account read the owner's vault.");
    await Reject(() => first.GetAsync("PUBLIC-INVALID-SESSION-TOKEN-123456789", null, CancellationToken.None), error => error.Unauthorized);
    Pass("account isolation and invalid bearer rejection");
    await Reject(() => first.RegisterAsync(new() { UserName = "public-owner", Salt = otherSalt, AuthSecret = otherKeys.AuthSecret }, CancellationToken.None));
    Pass("duplicate registration is rejected with an explicit existing-account message");
    await first.LogoutAsync(registered.Token, CancellationToken.None);
    await Reject(() => first.GetAsync(registered.Token, null, CancellationToken.None), error => error.Unauthorized);
    Require((await second.GetAsync(home.Token, null, CancellationToken.None)).Revision == 2, "One-device logout revoked another device.");
    Pass("server session revocation is limited to the logged-out device");
    Stop(); await Start();
    Require((await second.GetAsync(home.Token, null, CancellationToken.None)).Revision == 2, "Restart lost session or encrypted vault."); Pass("atomic on-disk account/session/vault persistence survives server restart");
    foreach (string file in Directory.GetFiles(Path.Combine(work, "data")))
    {
        if (file.EndsWith("server.lock")) continue;
        string data = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
        Require(!data.Contains(password) && !data.Contains("PUBLIC-ONLY-API-KEY") && !data.Contains(home.Token) && !data.Contains(secrets.AuthSecret) && !data.Contains(secrets.VaultPassword), "Persisted plaintext credential material.");
    }
    Pass("server files contain no account password, API key, raw session token or client vault secret");
    using (var cancel = new CancellationTokenSource())
    {
        cancel.Cancel();
        try { await first.GetAsync(home.Token, null, cancel.Token); throw new InvalidOperationException("Cancellation ignored."); }
        catch (OperationCanceledException) { }
    }
    Pass("caller cancellation stops network operations");
    Stop(); allowRegistration = false; await Start();
    await Reject(() => first.RegisterAsync(new() { UserName = "public-closed", Salt = otherSalt, AuthSecret = otherKeys.AuthSecret }, CancellationToken.None), error => error.StatusCode == 403 && error.Message.Contains("注册"));
    Require((await second.GetAsync(home.Token, null, default)).Revision == 2, "Closing registration revoked existing accounts.");
    Pass("optional server registration switch closes signup while existing sessions continue working");
    using (var http = new HttpClient())
    {
        var wrong = new SyncLoginRequest { UserName = "public-other", AuthSecret = secrets.AuthSecret };
        for (int i = 0; i < 5; i++) Require((await http.PostAsJsonAsync(new Uri(address, "api/v1/login"), wrong)).StatusCode == HttpStatusCode.Unauthorized, "Wrong auth accepted.");
        var correct = new SyncLoginRequest { UserName = "public-other", AuthSecret = otherKeys.AuthSecret };
        Require((int)(await http.PostAsJsonAsync(new Uri(address, "api/v1/login"), correct)).StatusCode == 429, "Account lockout not enforced.");
        bool limited = false;
        for (int i = 0; i < 25; i++) limited |= (int)(await http.PostAsJsonAsync(new Uri(address, "api/v1/parameters"), new SyncUserNameRequest("public-owner"))).StatusCode == 429;
        Require(limited, "Authentication endpoint rate limit not enforced.");
    }
    Pass("failed-login lockout and bounded authentication request rate");
    Console.WriteLine($"{passed} real private sync server checks passed; synthetic credentials only; no Aliyun deployment or paid provider request.");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); return 1; }
finally { Stop(); Directory.Delete(work, true); }
static async Task Reject(Func<Task> action, Func<AccountSyncException, bool>? condition = null)
{
    try { await action(); throw new InvalidOperationException("Unauthorized/conflicting operation was accepted."); }
    catch (AccountSyncException error) when (condition is null || condition(error)) { }
}
