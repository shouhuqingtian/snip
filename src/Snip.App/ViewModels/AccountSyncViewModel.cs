using System.Windows.Threading;
using Snip.App.Mvvm;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.App.ViewModels;

public sealed class AccountSyncViewModel : ObservableObject, IDisposable
{
    private readonly AccountSyncService service;
    private readonly Dispatcher dispatcher;
    private readonly CancellationTokenSource lifetime = new();
    private string server, userName, password = "", confirmation = "";
    private bool busy, registering;
    private string status;
    private AccountAuthenticationNotice? localNotice;
    public AccountSyncViewModel(AccountSyncService service, Dispatcher dispatcher)
    {
        this.service = service; this.dispatcher = dispatcher;
        var state = service.State;
        server = string.IsNullOrWhiteSpace(state.Server) ? AccountSyncProtocol.DefaultServer : state.Server;
        userName = state.UserName; status = state.Status;
        LoginCommand = new(async () =>
        {
            localNotice = null;
            try
            {
                NormalizeServer();
                _ = AccountSyncProtocol.UserName(UserName);
                if (Password.Length is < 12 or > 256) throw new ArgumentException("密码须为 12–256 个字符。");
                if (Registering && Password != Confirmation) throw new ArgumentException("两次输入的密码不一致，请检查后注册。");
            }
            catch (ArgumentException error) { ReportValidation(error.Message); return; }
            Busy = true;
            try { await service.AuthenticateAsync(Server, UserName, Password, Registering, lifetime.Token); }
            finally
            {
                if (SignedIn) { Password = ""; Confirmation = ""; SecretsCleared?.Invoke(this, EventArgs.Empty); }
                Status = service.State.Status; Busy = false; Refresh();
            }
        }, Report, () => !Busy && !SignedIn);
        SyncCommand = Action(() => service.SynchronizeAsync(token: lifetime.Token), () => SignedIn);
        UseCloudCommand = Action(() => service.SynchronizeAsync(AccountSyncResolution.UseCloud, lifetime.Token), () => SignedIn && Conflict);
        UseLocalCommand = Action(() => service.SynchronizeAsync(AccountSyncResolution.UseLocal, lifetime.Token), () => SignedIn && Conflict);
        LogoutCommand = Action(service.LogoutAsync, () => SignedIn);
        service.Changed += ServiceChanged;
    }
    public event EventHandler? SecretsCleared;
    public string Server { get => server; set => Set(ref server, value); }
    public string UserName { get => userName; set => Set(ref userName, value); }
    public string Password { get => password; set => Set(ref password, value); }
    public string Confirmation { get => confirmation; set => Set(ref confirmation, value); }
    public bool Registering
    {
        get => registering;
        set { if (Set(ref registering, value)) { Notify(nameof(LoginText)); Notify(nameof(LoggingIn)); } }
    }
    public bool LoggingIn { get => !Registering; set { if (value) Registering = false; } }
    public string LoginText => Busy ? SignedIn ? "正在同步…" : Registering ? "正在注册…" : "正在登录…" : Registering ? "注册并登录" : "登录";
    public bool SignedIn => service.State.SignedIn;
    public bool ShowCredentials => !SignedIn;
    public bool Conflict => service.State.Conflict;
    public bool SyncProblem => service.State.SynchronizationFailed;
    public string SyncHeading => SyncProblem ? "同步失败，可点击立即同步重试" : Conflict ? "同步已暂停，请选择保留方向" : "配置同步";
    public bool CanEdit => !Busy && !SignedIn;
    public bool CanToggleAutomatic => !Busy && SignedIn;
    public bool Busy { get => busy; private set { if (Set(ref busy, value)) Refresh(); } }
    public string Status { get => status; private set => Set(ref status, value); }
    public string AccountSummary => SignedIn ? "已登录：" + service.State.UserName : "尚未登录";
    private AccountAuthenticationNotice? Notice => localNotice ?? service.State.Authentication;
    public string FeedbackTitle => Notice?.Title ?? (SignedIn ? "已登录" : "请登录或注册");
    public string FeedbackDetail => Notice?.Detail ?? (SignedIn ? "此设备已登录，配置同步结果显示在下方。" : "已有账号直接登录；第一次使用请选择“注册新账号”。");
    public bool FeedbackError => Notice?.Kind == AccountNoticeKind.Error;
    public bool FeedbackSuccess => Notice?.Kind == AccountNoticeKind.Success || Notice is null && SignedIn;
    public bool FeedbackWarning => Notice?.Kind == AccountNoticeKind.Warning;
    public string FeedbackSymbol => FeedbackError || FeedbackWarning ? "!" : FeedbackSuccess ? "✓" : "i";
    public bool Automatic
    {
        get => service.State.Automatic;
        set { if (CanToggleAutomatic && value != Automatic) _ = ChangeAutomaticAsync(value); }
    }
    public void NormalizeServer() => Server = AccountSyncProtocol.Server(string.IsNullOrWhiteSpace(Server) ? AccountSyncProtocol.DefaultServer : Server).AbsoluteUri;
    public void NormalizeServerIfValid() { try { NormalizeServer(); } catch (ArgumentException) { /* Validate explicitly on submit; do not interrupt typing. */ } }
    private async Task ChangeAutomaticAsync(bool value)
    { Busy = true; try { await service.SetAutomaticAsync(value); } catch (Exception error) { Report(error); } finally { Busy = false; } }
    public AsyncCommand LoginCommand { get; }
    public AsyncCommand SyncCommand { get; }
    public AsyncCommand UseCloudCommand { get; }
    public AsyncCommand UseLocalCommand { get; }
    public AsyncCommand LogoutCommand { get; }
    private AsyncCommand Action(Func<Task> action, Func<bool> available) => new(async () =>
    { Busy = true; try { await action(); } finally { Status = service.State.Status; Busy = false; Refresh(); } }, Report, () => !Busy && available());
    private void ReportValidation(string detail)
    {
        localNotice = new(AccountNoticeKind.Error, Registering ? "注册未提交" : "登录未提交", detail);
        Status = "尚未开始同步。"; Refresh();
    }
    private void Report(Exception _)
    {
        if (lifetime.IsCancellationRequested) return;
        if (!SignedIn) localNotice = new(AccountNoticeKind.Error, Registering ? "注册未完成" : "登录未完成", "请检查服务器地址和网络后重试，本机配置保留。");
        Status = "操作未完成，本机配置保留；请稍后重试。"; Refresh();
    }
    private void ServiceChanged(object? sender, EventArgs args)
    {
        if (dispatcher.HasShutdownStarted || lifetime.IsCancellationRequested) return;
        dispatcher.BeginInvoke(() =>
        {
            if (lifetime.IsCancellationRequested) return;
            var state = service.State; Status = state.Status;
            if (state.SignedIn) { Server = state.Server; UserName = state.UserName; }
            Refresh();
        });
    }
    private void Refresh()
    {
        Notify(nameof(SignedIn)); Notify(nameof(ShowCredentials)); Notify(nameof(Conflict)); Notify(nameof(CanEdit)); Notify(nameof(CanToggleAutomatic)); Notify(nameof(Automatic)); Notify(nameof(AccountSummary));
        Notify(nameof(SyncProblem)); Notify(nameof(SyncHeading)); Notify(nameof(FeedbackTitle)); Notify(nameof(FeedbackDetail)); Notify(nameof(FeedbackError)); Notify(nameof(FeedbackSuccess)); Notify(nameof(FeedbackWarning)); Notify(nameof(FeedbackSymbol)); Notify(nameof(LoginText));
        LoginCommand.Refresh(); SyncCommand.Refresh(); UseCloudCommand.Refresh(); UseLocalCommand.Refresh(); LogoutCommand.Refresh();
    }
    public void Dispose()
    {
        service.Changed -= ServiceChanged; lifetime.Cancel(); Password = ""; Confirmation = "";
    }
}
