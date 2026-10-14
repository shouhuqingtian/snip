using Snip.App.Mvvm;
using Snip.Core;

namespace Snip.App.ViewModels;

public sealed class HotkeySettingsViewModel : ObservableObject
{
    private readonly IHotkeyService service;
    private readonly IHotkeySettingsStore store;
    private HotkeyGesture draft;
    private string status = "点选输入框，再按下需要的快捷键。保存后立即生效。";
    public HotkeySettingsViewModel(IHotkeyService service, IHotkeySettingsStore store, HotkeyGesture initial)
    {
        this.service = service; this.store = store; draft = initial;
        SaveCommand = new(Save);
        ResetCommand = new(() => SetGesture(HotkeyGesture.Default));
    }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ResetCommand { get; }
    public event EventHandler<HotkeyGesture>? Saved;
    public string GestureText => draft.DisplayText;
    public string Status { get => status; private set => Set(ref status, value); }
    public void SetGesture(HotkeyGesture value)
    {
        try { value.Validate(); draft = value; Notify(nameof(GestureText)); Status = "点击保存使 " + value.DisplayText + " 生效。"; }
        catch (ArgumentException error) { Status = error.Message; }
    }
    private void Save()
    {
        var previous = service.Current;
        try
        {
            if (!service.Register(draft)) { Status = "快捷键已被其他程序占用或由系统保留，请换一个组合；原快捷键保留。"; return; }
            try { store.Save(draft); }
            catch
            {
                bool restored = previous is not null && service.Register(previous);
                Status = restored ? "保存配置失败，已恢复原快捷键；请检查本地设置目录权限。"
                    : "保存配置失败，当前快捷键仅本次有效，重启后不会保留。";
                if (!restored) Saved?.Invoke(this, draft);
                return;
            }
            Status = "已保存，" + draft.DisplayText + " 已生效，重启后保留。";
            Saved?.Invoke(this, draft);
        }
        catch (Exception) { Status = "无法修改快捷键，请重试或导出诊断包。"; }
    }
}
