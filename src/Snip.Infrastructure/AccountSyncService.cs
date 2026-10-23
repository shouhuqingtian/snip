using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Snip.Core;

namespace Snip.Infrastructure;

public enum AccountSyncResolution { Automatic, UseCloud, UseLocal }
public enum AccountNoticeKind { Working, Success, Warning, Error }
public sealed record AccountAuthenticationNotice(AccountNoticeKind Kind, string Title, string Detail);
public sealed record AccountSyncState(bool SignedIn, string Server, string UserName, bool Automatic, bool Conflict, string Status)
{
    public AccountAuthenticationNotice? Authentication { get; init; }
    public bool SynchronizationFailed { get; init; }
}

public sealed class AccountSyncService : IDisposable
{
    private readonly string path;
    private readonly SettingsConfigurationTransfer transfer;
    private readonly Dispatcher dispatcher;
    private readonly Func<bool> canApply;
    private readonly Action refreshed;
    private readonly DiagnosticJournal? diagnostics;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private long generation;
    private Stored? stored;
    private Session? session;
    private Task? loop;
    private int started, failures;
    private bool synchronizationFailed;
    private AccountAuthenticationNotice? authentication;
    private DateTimeOffset nextAttempt;
    private AccountSyncState state = new(false, "", "", true, false, "登录同一个自用截图账号，即可在两台电脑同步密钥和服务配置。");
    private sealed class Stored
    {
        public int Version { get; set; } = 1;
        public string Server { get; set; } = "";
        public string UserName { get; set; } = "";
        public string ProtectedToken { get; set; } = "";
        public string ProtectedVaultPassword { get; set; } = "";
        public DateTimeOffset ExpiresUtc { get; set; }
        public long Revision { get; set; } = -1;
        public string Fingerprint { get; set; } = "";
        public bool Automatic { get; set; } = true;
        public override string ToString() => "Protected sync settings (redacted)";
    }
    private sealed class Session(string token, string vaultPassword)
    {
        public string Token { get; } = token;
        public string VaultPassword { get; } = vaultPassword;
        public override string ToString() => "Sync session (redacted)";
    }
    public AccountSyncService(string directory, Dispatcher dispatcher, Func<bool> canApply, Action refreshed, DiagnosticJournal? diagnostics = null)
    {
        path = Path.Combine(directory, "account-sync.json"); transfer = new(directory, includeDeviceSettings: false);
        this.dispatcher = dispatcher; this.canApply = canApply; this.refreshed = refreshed; this.diagnostics = diagnostics;
    }
    public event EventHandler? Changed;
    public AccountSyncState State => Volatile.Read(ref state);
    public Task StartAsync() => Task.Run(() =>
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        try { transfer.RecoverInterruptedImport(); }
        catch (Exception error)
        {
            diagnostics?.Record("account-sync", "transaction-recovery-failed", error: error);
            throw new InvalidOperationException("上次同步的配置恢复尚未完成，请解除文件占用并检查配置目录权限后重启；密钥备份未删除。");
        }
        try
        {
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 16384) throw new InvalidDataException();
                stored = JsonSerializer.Deserialize<Stored>(File.ReadAllBytes(path)) ?? throw new InvalidDataException();
                if (stored.Version != 1 || stored.Revision < -1 || stored.Fingerprint is null || stored.Fingerprint.Length > 64 || stored.Server is null || stored.UserName is null) throw new InvalidDataException();
                _ = AccountSyncProtocol.Server(stored.Server); _ = AccountSyncProtocol.UserName(stored.UserName);
                if (stored.ExpiresUtc > DateTimeOffset.UtcNow && stored.ProtectedToken.Length > 0)
                    session = new(Unprotect(stored.ProtectedToken), Unprotect(stored.ProtectedVaultPassword));
                Publish(session is null ? "会话已过期，请重新登录；本机服务配置保留。" : "已登录，正在检查同步配置。");
            }
        }
        catch (Exception error)
        {
            stored = null; session = null;
            diagnostics?.Record("account-sync", "session-read-failed", error: error);
            Publish("账号同步设置无法读取；本机翻译和语音配置仍可使用。请重新登录。");
        }
        loop = Task.Run(LoopAsync);
    });
    public Task AuthenticateAsync(string serverAddress, string userName, string password, bool register, CancellationToken token = default)
    {
        long current = Interlocked.Read(ref generation);
        return Task.Run(async () =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            using var running = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token); operation = running;
            bool accountCreated = false, authenticated = false;
            authentication = new(AccountNoticeKind.Working, register ? "正在注册…" : "正在登录…", "正在验证账号，请稍候。");
            Publish("账号验证完成后开始同步配置。", conflict: false);
            try
            {
                var server = AccountSyncProtocol.Server(serverAddress); string name = AccountSyncProtocol.UserName(userName);
                var api = new AccountSyncApi(server);
                string salt = register ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)) : (await api.ParametersAsync(name, running.Token).ConfigureAwait(false)).Salt;
                var secrets = AccountSyncProtocol.Derive(password, salt);
                var response = register
                    ? await api.RegisterAsync(new() { UserName = name, Salt = salt, AuthSecret = secrets.AuthSecret }, running.Token).ConfigureAwait(false)
                    : await api.LoginAsync(new() { UserName = name, AuthSecret = secrets.AuthSecret }, running.Token).ConfigureAwait(false);
                accountCreated = register;
                EnsureCurrent(current, running.Token);
                bool sameAccount = stored?.Server == server.AbsoluteUri && stored.UserName == name;
                var next = new Stored
                {
                    Server = server.AbsoluteUri, UserName = name, ProtectedToken = Protect(response.Token), ProtectedVaultPassword = Protect(secrets.VaultPassword), ExpiresUtc = response.ExpiresUtc,
                    Automatic = stored?.Automatic ?? true, Revision = sameAccount ? stored!.Revision : -1, Fingerprint = sameAccount ? stored!.Fingerprint : ""
                };
                Save(next); stored = next; session = new(response.Token, secrets.VaultPassword);
                authenticated = true;
                authentication = new(AccountNoticeKind.Success, register ? "注册成功，已自动登录" : "登录成功",
                    register ? "账号已创建并登录此设备。配置同步结果显示在下方。" : "已登录此设备。配置同步结果显示在下方。");
                Publish("已登录，正在同步配置。", conflict: false);
                await SynchronizeLockedAsync(AccountSyncResolution.Automatic, current, running.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!lifetime.IsCancellationRequested && current == Interlocked.Read(ref generation))
                {
                    if (!authenticated) authentication = new(AccountNoticeKind.Warning, accountCreated ? "注册成功，登录已取消" : "操作已取消",
                        accountCreated ? "账号已创建，下次请选择登录。" : "尚未确认账号操作是否完成；可以尝试直接登录。");
                    Publish("操作已取消，本机配置保留。");
                }
            }
            catch (Exception error)
            {
                if (!authenticated)
                {
                    bool unconfirmed = error is AccountSyncException { StatusCode: 0 or >= 500 };
                    authentication = new(accountCreated ? AccountNoticeKind.Warning : unconfirmed ? AccountNoticeKind.Warning : AccountNoticeKind.Error,
                        accountCreated ? "注册成功，本机登录状态未保存" : unconfirmed ? register ? "注册结果未确认" : "登录未完成" : register ? "注册失败" : "登录失败",
                        accountCreated ? "账号已创建，请选择登录后重试；本机配置保留。" : SafeError(error, "请检查服务器地址和网络后重试。") +
                        (unconfirmed && register ? " 如果刚才已经提交，可切换到登录尝试，无需重复注册。" : ""));
                }
                Fail(error, authenticated ? "配置同步未完成，本机配置保留；可点击立即同步重试。" : "账号操作未完成，本机配置保留。");
            }
            finally { if (operation == running) operation = null; gate.Release(); }
        }, token);
    }
    public Task SynchronizeAsync(AccountSyncResolution resolution = AccountSyncResolution.Automatic, CancellationToken token = default)
    {
        long current = Interlocked.Read(ref generation);
        return Task.Run(async () =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            using var running = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token); operation = running;
            try { await SynchronizeLockedAsync(resolution, current, running.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception error) { Fail(error, "同步未完成，已保留本机配置；稍后可重试。"); }
            finally { if (operation == running) operation = null; gate.Release(); }
        }, token);
    }
    private async Task SynchronizeLockedAsync(AccountSyncResolution resolution, long current, CancellationToken token)
    {
        if (stored is null || session is null) return;
        EnsureCurrent(current, token);
        if (resolution == AccountSyncResolution.Automatic && State.Conflict) return;
        synchronizationFailed = false;
        Publish("正在检查本机与云端配置。");
        var api = new AccountSyncApi(AccountSyncProtocol.Server(stored.Server));
        var local = transfer.Capture();
        var remote = await api.GetAsync(session.Token, resolution == AccountSyncResolution.Automatic && stored.Revision >= 0 ? stored.Revision : null, token).ConfigureAwait(false);
        EnsureCurrent(current, token);
        if (resolution == AccountSyncResolution.Automatic && remote.Revision > 0 && remote.Revision < stored.Revision)
        { Publish("云端版本发生回退，本机配置保留；请确认后选择同步方向。", conflict: true); return; }
        bool first = stored.Revision < 0;
        bool localChanged = local.Fingerprint != stored.Fingerprint;
        bool remoteChanged = remote.Revision != stored.Revision;
        if (resolution == AccountSyncResolution.Automatic && remote.Revision > 0 && remoteChanged && localChanged && (!first || transfer.HasProfiles(local)))
        { Publish("本机和云端都有配置，请选择保留云端或本机；自动同步已暂停。", conflict: true); return; }
        if (resolution == AccountSyncResolution.Automatic && remote.Revision == 0 && !first)
        { Publish("云端配置已不存在；本机配置保留，请选择上传本机配置。", conflict: true); return; }
        failures = 0; nextAttempt = default;
        bool download = resolution == AccountSyncResolution.UseCloud || resolution == AccountSyncResolution.Automatic && remoteChanged && remote.Revision > 0;
        if (download)
        {
            if (remote.Archive is null) { Publish("云端还没有配置，请选择上传本机配置。", conflict: true); return; }
            SettingsConfigurationTransfer.Prepared prepared;
            try { prepared = transfer.Decrypt(remote.Archive, session.VaultPassword, remote.Revision); }
            catch (Exception error) when (error is CryptographicException or InvalidOperationException)
            { throw new AccountSyncException("云端配置无法解密、版本不符或内容无效；本机未修改，可选择上传本机配置替换云端。", conflict: true); }
            SettingsConfigurationTransfer.Snapshot? applied = null;
            Publish("云端配置已验证，正在应用到本机。");
            await dispatcher.InvokeAsync(() =>
            {
                EnsureCurrent(current, token);
                if (!canApply()) throw new InvalidOperationException("请先关闭在线设置、AI 语音、通用设置和配置迁移窗口，再同步云端配置；未保存的编辑会保留。");
                applied = transfer.Apply(prepared, local.Fingerprint, () => current == Interlocked.Read(ref generation) && !token.IsCancellationRequested);
                try { refreshed(); }
                catch (Exception error) { diagnostics?.Record("account-sync", "workbench-refresh-failed", error: error); }
            });
            EnsureCurrent(current, token);
            stored.Revision = remote.Revision; stored.Fingerprint = applied!.Fingerprint; Save(stored);
            Publish($"已同步云端配置：{prepared.TranslationProfiles} 组翻译 / OCR、{prepared.SpeechProfiles} 组 AI 语音。", conflict: false);
            diagnostics?.Record("account-sync", "download-applied", new { prepared.TranslationProfiles, prepared.SpeechProfiles });
            return;
        }
        if (resolution == AccountSyncResolution.UseLocal || localChanged || first)
        {
            byte[] archive = transfer.Encrypt(local, session.VaultPassword, checked(remote.Revision + 1));
            long updated = await api.PutAsync(session.Token, remote.Revision, archive, token).ConfigureAwait(false);
            EnsureCurrent(current, token);
            stored.Revision = updated; stored.Fingerprint = local.Fingerprint; Save(stored);
            Publish("本机密钥与服务配置已加密同步到账号。", conflict: false);
            diagnostics?.Record("account-sync", "upload-completed", new { Revision = updated });
        }
        else Publish("配置已是最新。", conflict: false);
    }
    public Task SetAutomaticAsync(bool enabled) => Task.Run(async () =>
    {
        await gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            if (stored is null) return;
            bool previous = stored.Automatic; stored.Automatic = enabled;
            try { Save(stored); } catch { stored.Automatic = previous; throw; }
            Publish(enabled ? "已开启自动同步，约每 2 小时检查一次；也可点击立即同步。" : "已暂停自动同步；可手动点击立即同步。");
        }
        catch (Exception error) { Fail(error, "同步开关保存失败。"); }
        finally { gate.Release(); }
    });
    public async Task LogoutAsync()
    {
        Interlocked.Increment(ref generation); CancelRunning();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var oldSession = session; var oldStored = stored; session = null; stored = null;
            bool cleared = true;
            try { await Task.Run(() => { if (File.Exists(path)) File.Delete(path); }).ConfigureAwait(false); }
            catch (Exception error) { cleared = false; diagnostics?.Record("account-sync", "local-session-delete-failed", error: error); }
            synchronizationFailed = false;
            authentication = new(cleared ? AccountNoticeKind.Success : AccountNoticeKind.Warning,
                cleared ? "已退出登录" : "退出未完全完成", cleared ? "本机翻译、OCR 和语音配置保留。" : "本次运行已停止同步，但本机会话文件未能删除，请检查目录权限。");
            Publish(cleared ? "已退出此设备的同步账号；本机翻译、OCR 和语音配置保留。" : "已停止本次运行的同步，但本机会话文件未能删除；请检查目录权限后再次退出，避免重启后恢复登录。", conflict: false);
            if (oldSession is not null && oldStored is not null)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await new AccountSyncApi(AccountSyncProtocol.Server(oldStored.Server)).LogoutAsync(oldSession.Token, deadline.Token).ConfigureAwait(false); }
                catch (Exception) { /* Local logout is final even when remote revocation is temporarily unreachable. */ }
            }
        }
        finally { gate.Release(); }
    }
    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(2));
        try
        {
            do
            {
                if (session is not null && stored?.Automatic == true && !State.Conflict && DateTimeOffset.UtcNow >= nextAttempt)
                    await SynchronizeAsync(token: lifetime.Token).ConfigureAwait(false);
            } while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }
    private void Publish(string status, bool? conflict = null)
    {
        Volatile.Write(ref state, new(session is not null, stored?.Server ?? "", stored?.UserName ?? "", stored?.Automatic ?? true, conflict ?? State.Conflict, status) { Authentication = authentication, SynchronizationFailed = synchronizationFailed });
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void Fail(Exception error, string fallback)
    {
        diagnostics?.Record("account-sync", "operation-failed", error is AccountSyncException syncError
            ? new { syncError.StatusCode, NetworkError = syncError.NetworkError?.ToString() } : null, error: error);
        failures = Math.Min(failures + 1, 4); nextAttempt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, 30 * (1 << failures)));
        synchronizationFailed = session is not null;
        if (error is AccountSyncException { Unauthorized: true })
        {
            if (session is not null) authentication = new(AccountNoticeKind.Warning, "登录已失效", "请重新登录；本机服务配置保留。");
            session = null; synchronizationFailed = false;
            if (stored is not null)
            {
                stored.ProtectedToken = ""; stored.ProtectedVaultPassword = "";
                try { Save(stored); } catch (Exception) { }
            }
        }
        Publish(SafeError(error, fallback),
            error is AccountSyncException { Conflict: true } ? true : null);
    }
    private static string SafeError(Exception error, string fallback) =>
        error is AccountSyncException or ArgumentException or CryptographicException or InvalidOperationException ? error.Message : fallback;
    private void CancelRunning()
    {
        try { Volatile.Read(ref operation)?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
    private void EnsureCurrent(long current, CancellationToken token)
    { token.ThrowIfCancellationRequested(); if (current != Interlocked.Read(ref generation)) throw new OperationCanceledException(); }
    private void Save(Stored value) => SettingsConfigurationTransfer.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(value));
    private static string Protect(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static string Unprotect(string value)
    {
        byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Dispose()
    {
        Interlocked.Increment(ref generation); lifetime.Cancel(); CancelRunning();
        session = null;
    }
}
