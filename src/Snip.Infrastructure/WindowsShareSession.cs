using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Snip.Infrastructure;

public interface IWindowsShareSession : IDisposable
{
    Task PrepareAsync(BitmapSource image, CancellationToken token);
    void Show();
}

/// <summary>Owns a HWND-bound native share request. The OS, not this app, selects recipients.</summary>
public sealed class WindowsShareSession : IWindowsShareSession
{
    private readonly IntPtr owner;
    private readonly DataTransferManager manager;
    private StorageFile? file;
    private bool disposed, shown;
    public WindowsShareSession(IntPtr owner)
    {
        if (owner == IntPtr.Zero) throw new ArgumentException("分享窗口尚未初始化。", nameof(owner));
        if (!DataTransferManager.IsSupported()) throw new NotSupportedException("当前 Windows 环境不支持系统分享。可复制图片或保存 PNG 后手动发送。");
        this.owner = owner;
        manager = DataTransferManagerInterop.GetForWindow(owner);
        manager.DataRequested += OnDataRequested;
    }
    public async Task PrepareAsync(BitmapSource image, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!image.IsFrozen) throw new ArgumentException("分享截图必须先冻结。", nameof(image));
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SelfSnip", "ShareCache");
        string path = Path.Combine(folder, "capture-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested(); Directory.CreateDirectory(folder);
                // Targets may read after the panel closes. Expire leftovers on the next share.
                foreach (string old in Directory.EnumerateFiles(folder, "capture-*.png"))
                    try { if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1)) File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                token.ThrowIfCancellationRequested(); new ImageExportService().SavePng(image, path);
            }, token);
            token.ThrowIfCancellationRequested();
            var prepared = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
            token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(disposed, this);
            if (file is not null) throw new InvalidOperationException("分享截图已经准备好。");
            file = prepared;
        }
        catch { TryDelete(path); throw; }
    }
    public void Show()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (file is null) throw new InvalidOperationException("截图还在准备，请稍候。");
        DataTransferManagerInterop.ShowShareUIForWindow(owner);
        shown = true;
    }
    private void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        if (disposed || file is null) { args.Request.FailWithDisplayText("截图尚未准备好，请返回自用截图重试。"); return; }
        try { FillPackage(args.Request.Data, file); }
        catch (Exception) { args.Request.FailWithDisplayText("无法读取截图，请返回自用截图重试或保存 PNG。"); }
    }
    public static void FillPackage(DataPackage package, StorageFile image)
    {
        package.Properties.Title = "自用截图";
        package.Properties.Description = "PNG 截图";
        package.SetStorageItems(new[] { image }, readOnly: true);
        var reference = RandomAccessStreamReference.CreateFromFile(image);
        package.SetBitmap(reference); package.Properties.Thumbnail = reference;
    }
    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        manager.DataRequested -= OnDataRequested;
        // Keep a submitted file for asynchronous targets; never delete it on panel dismissal.
        if (!shown && file is not null) TryDelete(file.Path);
        file = null;
    }
}
