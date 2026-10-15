using Snip.App.Mvvm;
using Snip.Core;
namespace Snip.App.ViewModels;

public sealed class PinHotkeySettingsViewModel : ObservableObject
{
    private readonly IPinHotkeyService service;
    private readonly IPinHotkeySettingsStore store;
    private PinHotkeySettings draft;
    private string status="点击输入框后按组合键；直接按 Delete 或 Backspace 可清除。保存后立即生效，重启后保留。";
    public PinHotkeySettingsViewModel(IPinHotkeyService service,IPinHotkeySettingsStore store,PinHotkeySettings initial)
    {
        this.service=service;this.store=store;draft=initial;
        SaveCommand=new(Save);ResetCommand=new(()=>{draft=new();Changed();Status="已清空草稿，点击保存后解除贴图快捷键。";});
    }
    public string RestoreText=>draft.Restore?.DisplayText ?? "未设置";
    public string PreviousText=>draft.Previous?.DisplayText ?? "未设置";
    public string NextText=>draft.Next?.DisplayText ?? "未设置";
    public string Status { get=>status;private set=>Set(ref status,value); }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ResetCommand { get; }
    public void SetGesture(PinHotkeyAction action,HotkeyGesture? key)
    {
        try
        {
            key?.Validate();
            draft=action switch {PinHotkeyAction.Restore=>draft with {Restore=key},PinHotkeyAction.Previous=>draft with {Previous=key},PinHotkeyAction.Next=>draft with {Next=key},_=>throw new ArgumentOutOfRangeException(nameof(action))};
            Changed();Status="点击保存应用贴图快捷键。";
        }
        catch(ArgumentException error){Status=error.Message;}
    }
    private void Changed(){Notify(nameof(RestoreText));Notify(nameof(PreviousText));Notify(nameof(NextText));}
    private void Save()
    {
        var previous=service.Current;
        try
        {
            draft.Validate();
            if(!service.Apply(draft)){Status="快捷键已被截图、其他程序或系统占用；原贴图快捷键保留，请换一个组合。";return;}
            try { store.Save(draft); }
            catch
            {
                bool restored=service.Apply(previous);
                Status=restored ? "保存配置失败，已恢复原贴图快捷键。" : "保存失败，原贴图快捷键无法恢复，请重新设置。";return;
            }
            Status="已保存贴图快捷键，立即生效，重启后保留。";
        }
        catch(ArgumentException error){Status=error.Message;}
        catch(Exception){Status="贴图快捷键设置失败，请重新设置或导出诊断包。";}
    }
}
