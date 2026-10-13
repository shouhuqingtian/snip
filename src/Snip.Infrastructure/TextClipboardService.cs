using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Snip.Core;
using Forms = System.Windows.Forms;

namespace Snip.Infrastructure;

public sealed class TextClipboardService : ITextClipboardService
{
    public const int ClipboardBusy = unchecked((int)0x800401D0);

    public async Task CopyAsync(string text, CancellationToken cancellationToken, IProgress<int>? retries = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA || SynchronizationContext.Current is null)
            throw new InvalidOperationException("复制文字需要在桌面界面线程执行。");
        var data = new Forms.DataObject();
        data.SetText(text, Forms.TextDataFormat.UnicodeText);
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Both OleSetClipboard and OleFlushClipboard must succeed. Disable the framework's
                // Thread.Sleep retries: our awaited delays keep the WPF dispatcher responsive.
                Forms.Clipboard.SetDataObject(data, copy: true, retryTimes: 0, retryDelay: 0);
                return;
            }
            catch (ExternalException error) when (error.HResult == ClipboardBusy && attempt < 7)
            {
                retries?.Report(attempt + 1);
                await Task.Delay(100 + attempt * 50, cancellationToken);
            }
        }
    }
}
