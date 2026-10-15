using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Infrastructure;
namespace Snip.App.Services;

public sealed class PinManager : IDisposable
{
    private readonly IImageExportService export;
    private readonly ObservableCollection<PinEntryViewModel> entries=[];
    private readonly Dictionary<PinEntryViewModel,PinWindow> windows=[];
    private readonly long historyBudget;
    private readonly int historyLimit;
    private PinManagerWindow? manager;
    private PinEntryViewModel? current;
    private int sequence;
    private long closedSequence;
    private bool disposed;
    public PinManager(IImageExportService export,Action? settings=null,int historyLimit=10,long historyBudget=64L*1024*1024)
    {
        if(historyLimit<0 || historyBudget<0)throw new ArgumentOutOfRangeException(nameof(historyLimit));
        this.export=export;this.historyLimit=historyLimit;this.historyBudget=historyBudget;
        ViewModel=new(entries,RestoreLast,()=>Cycle(-1),()=>Cycle(1),ClearHistory,settings);
    }
    public PinManagerViewModel ViewModel { get; }
    public void Add(BitmapSource image)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(!image.IsFrozen){image=image.Clone();image.Freeze();}
        PinEntryViewModel entry=null!;
        entry=new(++sequence,image,()=>Show(entry),()=>Close(entry),()=>Recover(entry),()=>Forget(entry));
        entries.Insert(0,entry);
        try { Show(entry); }
        catch { entries.Remove(entry);ViewModel.Refresh();throw; }
    }
    private void Show(PinEntryViewModel entry)
    {
        if(disposed || !entries.Contains(entry))return;
        if(windows.TryGetValue(entry,out var existing))
        {
            if(existing.WindowState==WindowState.Minimized)existing.WindowState=WindowState.Normal;
            existing.SetClickThrough(false);PinWindowPlacement.Fit(existing);existing.Show();existing.Activate();SetCurrent(entry);return;
        }
        var window=new PinWindow(entry.Image,export);
        windows.Add(entry,window);
        window.Activated+=(_,_)=>SetCurrent(entry);
        window.InteractionChanged+=(_,_)=>{entry.ClickThrough=window.IsClickThrough;ViewModel.Refresh();};
        window.Closing+=(_,_)=>entry.Presentation=window.CapturePresentation();
        window.Closed+=(_,_)=>
        {
            windows.Remove(entry);entry.IsOpen=false;entry.IsCurrent=false;entry.ClickThrough=false;entry.ClosedOrder=++closedSequence;
            if(current==entry)current=null;
            if(!disposed){TrimHistory();ViewModel.Status=entries.Contains(entry) ? "贴图已关闭，可点击“恢复上一张”重新显示。" : "贴图已关闭；该图片超过恢复缓存上限，未保留关闭记录。";ViewModel.Refresh();}
        };
        try
        {
            window.Show();window.RestorePresentation(entry.Presentation);entry.IsOpen=true;SetCurrent(entry);window.Activate();
            ViewModel.Status="贴图已显示；点击缩略图可定位，滚轮缩放，Esc 关闭后可恢复。";
        }
        catch { window.Close();windows.Remove(entry);throw; }
        ViewModel.Refresh();
    }
    private void SetCurrent(PinEntryViewModel entry)
    {
        if(disposed)return;
        if(current!=entry){if(current is not null)current.IsCurrent=false;current=entry;entry.IsCurrent=true;}
        ViewModel.Refresh();
    }
    private void Close(PinEntryViewModel entry){if(windows.TryGetValue(entry,out var window))window.Close();}
    private void Recover(PinEntryViewModel entry){if(windows.TryGetValue(entry,out var window))window.SetClickThrough(false);}
    private void Forget(PinEntryViewModel entry){if(!entry.IsOpen){entries.Remove(entry);ViewModel.Refresh();}}
    public void RestoreLast()
    {
        if(disposed)return;
        var last=entries.Where(entry=>!entry.IsOpen).MaxBy(entry=>entry.ClosedOrder);
        if(last is null){ViewModel.Status="没有可恢复的已关闭贴图；截图后点击图钉创建。";return;}
        Show(last);
    }
    public void Cycle(int direction)
    {
        if(disposed)return;
        var open=entries.Where(entry=>entry.IsOpen).Reverse().ToArray();
        if(open.Length==0){ViewModel.Status="没有正在显示的贴图，可先恢复上一张。";return;}
        int index=Array.IndexOf(open,current);
        int next=index<0 ? (direction<0 ? open.Length-1 : 0) : (index+(direction<0 ? -1 : 1)+open.Length)%open.Length;
        Show(open[next]);ViewModel.Status="已定位 "+open[next].Title+"；其他贴图保持显示。";
    }
    public void ClearHistory()
    {
        foreach(var entry in entries.Where(entry=>!entry.IsOpen).ToArray())entries.Remove(entry);
        ViewModel.Status="已清空本次运行的已关闭贴图记录。";ViewModel.Refresh();
    }
    private void TrimHistory()
    {
        var closed=entries.Where(entry=>!entry.IsOpen).OrderByDescending(entry=>entry.ClosedOrder).ToArray();
        long bytes=0;int count=0;
        foreach(var entry in closed)
        {
            long size=(long)entry.Image.PixelWidth*entry.Image.PixelHeight*4;
            if(count>=historyLimit || size>historyBudget-bytes)entries.Remove(entry);
            else{count++;bytes+=size;}
        }
    }
    public void ShowManager()
    {
        if(disposed)return;
        if(manager is null){manager=new(ViewModel);manager.Closed+=(_,_)=>manager=null;}
        manager.Show();if(manager.WindowState==WindowState.Minimized)manager.WindowState=WindowState.Normal;manager.Activate();
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;manager?.Close();
        foreach(var window in windows.Values.ToArray())window.Close();
        windows.Clear();entries.Clear();current=null;ViewModel.Refresh();
    }
}
