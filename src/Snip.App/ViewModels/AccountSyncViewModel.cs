using System.Windows.Threading;
using Snip.App.Mvvm;
using Snip.Infrastructure;

namespace Snip.App.ViewModels;

public sealed class AccountSyncViewModel : ObservableObject, IDisposable
{
    private readonly AccountSyncService service;
    private readonly Dispatcher dispatcher;
    private readonly CancellationTokenSource lifetime = new();
    private string server, userName, password = "", confirmation = "", invite = "";
    private bool busy, registering;
    private string status;
    public AccountSyncViewModel(AccountSyncService service, Dispatcher dispatcher)
    {
        this.service = service; this.dispatcher = dispatcher;
        var state = service.State; server = state.Server; userName = state.UserName; status = state.Status;
        LoginCommand = new(async () =>
        {
            if (Registering && Password != Confirmation) { Status = "两次输入的密码不一致，请检查后注册。"; return; }
            Busy = true;
            try { await service.AuthenticateAsync(Server, UserName, Password, InviteCode, Registering, lifetime.Token); }
            finally { Password = ""; Confirmation = ""; InviteCode = ""; SecretsCleared?.Invoke(this, EventArgs.Empty); Busy = false; }
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
    public string InviteCode { get => invite; set => Set(ref invite, value); }
    public bool Registering { get => registering; set { if (Set(ref registering, value)) { Notify(nameof(LoginText)); } } }
    public string LoginText => Registering ? "注册并登录" : "登录并同步";
    public bool SignedIn => service.State.SignedIn;
    public bool Conflict => service.State.Conflict;
    public bool CanEdit => !Busy && !SignedIn;
    public bool CanToggleAutomatic => !Busy && SignedIn;
    public bool Busy { get => busy; private set { if (Set(ref busy, value)) Refresh(); } }
    public string Status { get => status; private set => Set(ref status, value); }
    public string AccountSummary => SignedIn ? "已登录：" + service.State.UserName : "尚未登录";
    public bool Automatic
    {
        get => service.State.Automatic;
        set { if (CanToggleAutomatic && value != Automatic) _ = ChangeAutomaticAsync(value); }
    }
    private async Task ChangeAutomaticAsync(bool value)
    { Busy = true; try { await service.SetAutomaticAsync(value); } catch (Exception error) { Report(error); } finally { Busy = false; } }
    public AsyncCommand LoginCommand { get; }
    public AsyncCommand SyncCommand { get; }
    public AsyncCommand UseCloudCommand { get; }
    public AsyncCommand UseLocalCommand { get; }
    public AsyncCommand LogoutCommand { get; }
    private AsyncCommand Action(Func<Task> action, Func<bool> available) => new(async () =>
    { Busy = true; try { await action(); } finally { Busy = false; } }, Report, () => !Busy && available());
    private void Report(Exception _) => Status = "操作未完成，本机配置保留；请检查同步服务和网络。";
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
        Notify(nameof(SignedIn)); Notify(nameof(Conflict)); Notify(nameof(CanEdit)); Notify(nameof(CanToggleAutomatic)); Notify(nameof(Automatic)); Notify(nameof(AccountSummary));
        LoginCommand.Refresh(); SyncCommand.Refresh(); UseCloudCommand.Refresh(); UseLocalCommand.Refresh(); LogoutCommand.Refresh();
    }
    public void Dispose()
    {
        service.Changed -= ServiceChanged; lifetime.Cancel(); Password = ""; Confirmation = ""; InviteCode = "";
    }
}
