using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Snip.Core;
using Snip.Sync.Server;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = EncryptedConfigurationArchive.MaximumArchiveBytes);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddSingleton<AccountStore>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("vault", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 180, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
_ = app.Services.GetRequiredService<AccountStore>();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    try { await next(context); }
    catch (ArgumentException) { context.Response.StatusCode = 400; }
    catch (BadHttpRequestException error) { context.Response.StatusCode = error.StatusCode; }
    catch (Exception error)
    {
        app.Logger.LogWarning("Sync request failed: {ErrorType}, {ErrorCode}.", error.GetType().Name, error.HResult);
        if (context.Response.HasStarted) context.Abort(); else context.Response.StatusCode = 503;
    }
});
app.UseRateLimiter();
app.MapGet("/health", () => Results.Ok(new { status = "ok", version = 1 }));
app.MapPost("/api/v1/parameters", async (SyncUserNameRequest request, AccountStore store, CancellationToken token) =>
{
    var salt = await store.ParametersAsync(request.UserName, token);
    return salt is null ? Results.NotFound() : Results.Ok(new SyncAccountParameters(1, salt));
}).RequireRateLimiting("auth");
app.MapPost("/api/v1/register", async (SyncRegisterRequest request, AccountStore store, CancellationToken token) =>
{
    var result = await store.RegisterAsync(request, token);
    return result.Response is null ? Results.StatusCode(result.Code) : Results.Ok(result.Response);
}).RequireRateLimiting("auth");
app.MapPost("/api/v1/login", async (SyncLoginRequest request, AccountStore store, CancellationToken token) =>
{
    var result = await store.LoginAsync(request, token);
    return result.Response is null ? Results.StatusCode(result.Code) : Results.Ok(result.Response);
}).RequireRateLimiting("auth");
app.MapPost("/api/v1/logout", async (HttpContext context, AccountStore store, CancellationToken token) =>
{
    await store.LogoutAsync(Bearer(context), token); return Results.NoContent();
}).RequireRateLimiting("auth");
app.MapGet("/api/v1/vault", async (HttpContext context, AccountStore store, CancellationToken token) =>
{
    string? user = await store.AuthorizeAsync(Bearer(context), token); if (user is null) return Results.Unauthorized();
    var value = await store.ReadVaultAsync(user, token); if (value is null) return Results.NotFound();
    string etag = '"' + value.Value.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + '"';
    context.Response.Headers.ETag = etag;
    if (context.Request.Headers.IfNoneMatch == etag) return Results.StatusCode(304);
    return Results.Bytes(value.Value.Archive, "application/octet-stream");
}).RequireRateLimiting("vault");
app.MapPut("/api/v1/vault", async (HttpContext context, AccountStore store, CancellationToken token) =>
{
    string? user = await store.AuthorizeAsync(Bearer(context), token); if (user is null) return Results.Unauthorized();
    if (!long.TryParse(context.Request.Headers.IfMatch.ToString().Trim('"'), out long revision) || revision < 0) return Results.StatusCode(428);
    using var output = new MemoryStream(); var buffer = new byte[8192];
    while (true)
    {
        int read = await context.Request.Body.ReadAsync(buffer, token); if (read == 0) break;
        if (output.Length + read > EncryptedConfigurationArchive.MaximumArchiveBytes) return Results.StatusCode(413);
        output.Write(buffer, 0, read);
    }
    var archive = output.ToArray();
    if (archive.Length < 53 || !archive.AsSpan(0, 8).SequenceEqual("SNIPCFG1"u8)) return Results.BadRequest();
    long? updated = await store.WriteVaultAsync(user, revision, archive, token);
    if (updated is null) return Results.StatusCode(409);
    context.Response.Headers.ETag = '"' + updated.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + '"';
    return Results.NoContent();
}).RequireRateLimiting("vault");
app.Run();
static string Bearer(HttpContext context)
{
    var header = context.Request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.Ordinal) && header.Length <= 264 ? header[7..] : "";
}
