using System.Collections.ObjectModel;
using Snip.App.Mvvm;
namespace Snip.App.ViewModels;

public sealed class PinManagerViewModel : ObservableObject
{
    private string status="点击缩略图定位；关闭贴图后，可以恢复上一张。";
    internal PinManagerViewModel(ObservableCollection<PinEntryViewModel> entries, Action restore,Action previous,Action next,Action clear,Action? settings)
    {
        Entries=new(entries);
        RestoreCommand=new(restore,()=>Entries.Any(p=>!p.IsOpen));
        PreviousCommand=new(previous,()=>Entries.Any(p=>p.IsOpen));NextCommand=new(next,()=>Entries.Any(p=>p.IsOpen));
        ClearHistoryCommand=new(clear,()=>Entries.Any(p=>!p.IsOpen));SettingsCommand=new(()=>settings?.Invoke(),()=>settings is not null);
    }
    public ReadOnlyObservableCollection<PinEntryViewModel> Entries { get; }
    public string Status { get=>status; internal set=>Set(ref status,value); }
    public bool IsEmpty => Entries.Count==0;
    public string Summary => $"桌面显示 {Entries.Count(p=>p.IsOpen)} 张 · 可恢复 {Entries.Count(p=>!p.IsOpen)} 张";
    public RelayCommand RestoreCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }
    public RelayCommand SettingsCommand { get; }
    internal void Refresh()
    {
        Notify(nameof(IsEmpty));Notify(nameof(Summary));RestoreCommand.Refresh();PreviousCommand.Refresh();NextCommand.Refresh();ClearHistoryCommand.Refresh();
    }
}
