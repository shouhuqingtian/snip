using System.Windows.Media.Imaging;
using Snip.App.Mvvm;
using Snip.Core;
namespace Snip.App.ViewModels;

public sealed class PinEntryViewModel : ObservableObject
{
    private bool open, current, clickThrough;
    internal PinEntryViewModel(int number, BitmapSource image, Action show, Action close, Action recover, Action forget)
    {
        Number=number; Image=image;
        ShowCommand=new(show); CloseCommand=new(close,()=>IsOpen); RecoverInteractionCommand=new(recover,()=>IsOpen && ClickThrough); ForgetCommand=new(forget,()=>!IsOpen);
    }
    public int Number { get; }
    public BitmapSource Image { get; }
    public string Title => $"贴图 {Number} · {Image.PixelWidth}×{Image.PixelHeight}";
    internal long ClosedOrder { get; set; }
    internal PinPresentation? Presentation { get; set; }
    public bool IsOpen { get=>open; internal set { if(Set(ref open,value)){NotifyState();CloseCommand.Refresh();RecoverInteractionCommand.Refresh();ForgetCommand.Refresh();} } }
    public bool IsCurrent { get=>current; internal set { if(Set(ref current,value))NotifyState(); } }
    public bool ClickThrough { get=>clickThrough; internal set { if(Set(ref clickThrough,value)){NotifyState();RecoverInteractionCommand.Refresh();} } }
    public string StateText => IsOpen ? (IsCurrent ? "当前贴图 · " : "") + (ClickThrough ? "鼠标穿透" : "桌面显示") : "已关闭 · 可恢复";
    public string ShowText => IsOpen ? "定位" : "恢复";
    public RelayCommand ShowCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand RecoverInteractionCommand { get; }
    public RelayCommand ForgetCommand { get; }
    private void NotifyState(){Notify(nameof(StateText));Notify(nameof(ShowText));}
}

public sealed record PinPresentation(PixelRect Bounds,double ImageOpacity);
