using System.Windows.Threading;
using Snip.App.Views;
using Snip.Core;

namespace Snip.App.Services;

/// <summary>Defers feedback until the copy caller can close its overlays; at most one short-lived window.</summary>
public sealed class CopyNotificationService(Dispatcher dispatcher, DiagnosticJournal? diagnostics = null) : IDisposable
{
    private CopyNotificationWindow? current;
    private DispatcherOperation? pending;
    private long revision;
    private bool disposed;
    public void NotifyCopied()
    {
        dispatcher.VerifyAccess();
        if (disposed || dispatcher.HasShutdownStarted) return;
        pending?.Abort(); long version=++revision;
        pending=dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(() =>
        {
            pending=null;
            if (disposed || version!=revision || dispatcher.HasShutdownStarted) return;
            current?.Close();
            var window = new CopyNotificationWindow(); current=window;
            window.Closed += (_, _) => { if (ReferenceEquals(current,window)) current=null; };
            try { window.Show(); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            { window.Close(); diagnostics?.Record("clipboard-feedback","show-failed",error:error); }
        }));
    }
    public void Hide()
    {
        dispatcher.VerifyAccess(); revision++;
        pending?.Abort(); pending=null;
        current?.Close(); current=null;
    }
    public void Dispose() { if (disposed) return; Hide(); disposed=true; }
}
