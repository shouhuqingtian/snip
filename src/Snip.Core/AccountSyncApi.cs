using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Snip.Core;

/// <summary>No redirects, cookies or retries. Only derived authentication secrets and encrypted configuration leave the client.</summary>
public sealed class AccountSyncApi(Uri server, HttpClient? client = null)
{
    private static readonly HttpClient Shared = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient http = client ?? Shared;
    private readonly Uri root = AccountSyncProtocol.Server(server.AbsoluteUri);
    public async Task<SyncAccountParameters> ParametersAsync(string userName, CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/v1/parameters", new SyncUserNameRequest(AccountSyncProtocol.UserName(userName)), null, token).ConfigureAwait(false);
        var result = await JsonAsync<SyncAccountParameters>(response, token).ConfigureAwait(false);
        if (result.Version != AccountSyncProtocol.Version || !AccountSyncProtocol.ValidSalt(result.Salt)) throw new AccountSyncException("同步服务器版本或账号参数不受支持。");
        return result;
    }
    public async Task<SyncLoginResponse> RegisterAsync(SyncRegisterRequest request, CancellationToken token)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Post, "api/v1/register", request, null, token).ConfigureAwait(false);
            return await SessionAsync(response, token).ConfigureAwait(false);
        }
        catch (AccountSyncException error) when (error.Conflict) { throw new AccountSyncException("账号已存在，请切换到“登录”，使用原来的密码。", statusCode: 409); }
        catch (AccountSyncException error) when (error.StatusCode == 403) { throw new AccountSyncException("服务器尚未开放免邀请码注册。请更新服务器部署包，或检查注册开关。", statusCode: 403); }
    }
    public async Task<SyncLoginResponse> LoginAsync(SyncLoginRequest request, CancellationToken token)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Post, "api/v1/login", request, null, token).ConfigureAwait(false);
            return await SessionAsync(response, token).ConfigureAwait(false);
        }
        catch (AccountSyncException error) when (error.Unauthorized)
        { throw new AccountSyncException("账号或密码不正确，请检查后重新登录。", unauthorized: true, statusCode: 401); }
    }
    public async Task LogoutAsync(string bearer, CancellationToken token)
    { using var response = await SendAsync(HttpMethod.Post, "api/v1/logout", null, bearer, token).ConfigureAwait(false); }
    public async Task<SyncRemoteVault> GetAsync(string bearer, long? knownRevision, CancellationToken token)
    {
        using var request = Request(HttpMethod.Get, "api/v1/vault", bearer);
        if (knownRevision is { } revision) request.Headers.IfNoneMatch.Add(new('"' + revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + '"'));
        using var response = await ExecuteAsync(request, token, allowMissing: true).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified) return new(knownRevision ?? 0, null, true);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(0, null);
        long version = Revision(response);
        var archive = await ReadAsync(response, EncryptedConfigurationArchive.MaximumArchiveBytes, token).ConfigureAwait(false);
        if (archive.Length == 0) throw new AccountSyncException("云端配置为空，未修改本机配置。");
        return new(version, archive);
    }
    public async Task<long> PutAsync(string bearer, long expectedRevision, byte[] archive, CancellationToken token)
    {
        if (archive.Length == 0 || archive.Length > EncryptedConfigurationArchive.MaximumArchiveBytes) throw new AccountSyncException("同步配置超过大小上限。");
        using var request = Request(HttpMethod.Put, "api/v1/vault", bearer);
        request.Headers.IfMatch.Add(new('"' + expectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) + '"'));
        request.Content = new ByteArrayContent(archive); request.Content.Headers.ContentType = new("application/octet-stream");
        using var response = await ExecuteAsync(request, token).ConfigureAwait(false);
        return Revision(response);
    }
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string? bearer, CancellationToken token)
    {
        using var request = Request(method, path, bearer);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await ExecuteAsync(request, token).ConfigureAwait(false);
    }
    private HttpRequestMessage Request(HttpMethod method, string path, string? bearer)
    {
        var request = new HttpRequestMessage(method, new Uri(root, path));
        if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
        return request;
    }
    private async Task<HttpResponseMessage> ExecuteAsync(HttpRequestMessage request, CancellationToken token, bool allowMissing = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotModified || allowMissing && response.StatusCode == HttpStatusCode.NotFound) return response;
            int code = (int)response.StatusCode; response.Dispose();
            throw code switch
            {
                401 => new AccountSyncException("登录已过期，或账号认证不正确，请重新登录。", unauthorized: true, statusCode: code),
                403 => new AccountSyncException("服务器不允许此操作，请检查服务版本与注册设置。", statusCode: 403),
                404 => new AccountSyncException("账号不存在，或同步服务地址不正确。", statusCode: code),
                409 or 412 => new AccountSyncException("云端已有更新，或账号已存在；同步时请选择保留哪份配置。", conflict: true, statusCode: code),
                413 => new AccountSyncException("同步配置超过服务器大小上限。", statusCode: code),
                429 => new AccountSyncException("操作过于频繁，请稍后重试。", statusCode: code),
                >= 300 and < 400 => new AccountSyncException("同步地址返回了重定向，请填写最终 HTTPS 地址。", statusCode: code),
                _ => new AccountSyncException("同步服务未完成请求，请检查服务端部署和网络。", statusCode: code)
            };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new AccountSyncException("连接同步服务超时，本机配置保留。"); }
        catch (HttpRequestException error)
        {
            string detail = error.HttpRequestError switch
            {
                HttpRequestError.SecureConnectionError => "HTTPS 安全连接失败，请检查证书、TLS 配置和网络；使用 IP 地址时，证书须包含该 IP。",
                HttpRequestError.ProxyTunnelError => "无法通过代理连接同步服务，请检查本机代理是否运行及代理配置。",
                HttpRequestError.NameResolutionError => "无法解析同步服务器地址，请检查地址、DNS 和代理配置。",
                _ => "无法连接同步服务，请检查 HTTPS 地址、证书、网络和本机代理。"
            };
            throw new AccountSyncException(detail + " 本机配置保留。", innerException: error, networkError: error.HttpRequestError);
        }
    }
    private static long Revision(HttpResponseMessage response)
    {
        if (!long.TryParse(response.Headers.ETag?.Tag.Trim('"'), out long value) || value <= 0) throw new AccountSyncException("同步服务的配置版本无效。");
        return value;
    }
    private static async Task<T> JsonAsync<T>(HttpResponseMessage response, CancellationToken token)
    {
        byte[] bytes = await ReadAsync(response, 8192, token).ConfigureAwait(false);
        try { return JsonSerializer.Deserialize<T>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new JsonException(); }
        catch (JsonException) { throw new AccountSyncException("同步服务返回格式无效。"); }
    }
    private static async Task<SyncLoginResponse> SessionAsync(HttpResponseMessage response, CancellationToken token)
    {
        var result = await JsonAsync<SyncLoginResponse>(response, token).ConfigureAwait(false);
        if (result.Token is null || result.Token.Length is < 32 or > 256 || result.Token.Any(char.IsControl) || result.ExpiresUtc <= DateTimeOffset.UtcNow)
            throw new AccountSyncException("同步服务返回的会话无效。");
        return result;
    }
    private static async Task<byte[]> ReadAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(25));
        if (response.Content.Headers.ContentLength > maximum) throw new AccountSyncException("同步服务返回内容超过大小上限。");
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false); using var output = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false); if (read == 0) break;
                if (output.Length + read > maximum) throw new AccountSyncException("同步服务返回内容超过大小上限。");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new AccountSyncException("读取同步配置超时，本机配置保留。"); }
        catch (IOException) { throw new AccountSyncException("读取同步配置中断，本机配置保留，请稍后重试。"); }
        catch (HttpRequestException) { throw new AccountSyncException("读取同步配置中断，本机配置保留，请稍后重试。"); }
    }
}
