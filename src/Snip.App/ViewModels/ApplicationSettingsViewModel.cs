using Snip.App.Mvvm;
using Snip.Core;

namespace Snip.App.ViewModels;

public sealed class ApplicationSettingsViewModel : ObservableObject
{
    private readonly IAutoStartService startup;
    private readonly ITranslationPreferencesStore preferences;
    private bool startWithWindows;
    private TranslationMode mode;
    private string direction, status = "翻译方式需点击保存；开机自启开关即时生效。";
    public ApplicationSettingsViewModel(IAutoStartService startup, ITranslationPreferencesStore preferences, TranslationPreferences initial)
    {
        this.startup = startup; this.preferences = preferences; mode = initial.Mode; direction = initial.Direction;
        SaveCommand = new(Save);
        try { startWithWindows = startup.IsEnabled; }
        catch { status = "开机自启状态读取失败，请检查 Windows 用户权限。"; }
    }
    public bool StartWithWindows
    {
        get => startWithWindows;
        set
        {
            if (startWithWindows == value) return;
            try { startup.SetEnabled(value); Set(ref startWithWindows, value); Status = value ? "已登记开机自启，下次登录 Windows 后常驻托盘。" : "已关闭开机自启。"; }
            catch { Status = "开机自启设置失败，请确认程序文件夹和 Windows 用户权限。"; Notify(); }
        }
    }
    public TranslationMode Mode { get => mode; set { if (Enum.IsDefined(value)) Set(ref mode, value); } }
    public string Direction { get => direction; set { if (value is "auto" or "en-zh" or "zh-en") Set(ref direction, value); } }
    public string Status { get => status; private set => Set(ref status, value); }
    public RelayCommand SaveCommand { get; }
    private void Save()
    {
        try { preferences.Save(new(Mode, Direction)); Status = "默认翻译方式和方向已保存，新截图和重启软件后沿用；保存不会请求服务或加载模型。"; }
        catch { Status = "默认翻译设置保存失败，原设置保留；请检查设置目录权限。"; }
    }
}
