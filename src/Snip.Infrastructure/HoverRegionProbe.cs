using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Snip.Core;

namespace Snip.Infrastructure;

public interface IHoverRegionProbe : IDisposable
{
    Task<PixelRect?> FindAsync(WindowCaptureInfo target, PixelPoint point, CancellationToken token);
}

/// <summary>One reusable helper per selection session. No images, names or text cross the pipe.</summary>
public sealed class HoverRegionProbe(Func<ProcessStartInfo> host, DiagnosticJournal? diagnostics = null) : IHoverRegionProbe
{
    private readonly SemaphoreSlim gate = new(1);
    private Process? worker;
    private volatile bool disposed, unavailable;
    public event Action<int>? WorkerStarted;

    public Task<PixelRect?> FindAsync(WindowCaptureInfo target, PixelPoint point, CancellationToken token)
        => Task.Run(() => FindCoreAsync(target, point, token), token);

    private async Task<PixelRect?> FindCoreAsync(WindowCaptureInfo target, PixelPoint point, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (disposed || unavailable) return null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(worker is null ? TimeSpan.FromMilliseconds(2500) : TimeSpan.FromMilliseconds(900));
            try
            {
                var current = worker;
                if (current is null)
                {
                    var info = host(); info.UseShellExecute = false; info.CreateNoWindow = true;
                    info.RedirectStandardInput = info.RedirectStandardOutput = true;
                    info.ArgumentList.Add("--hover-worker");
                    current = new Process { StartInfo = info };
                    if (!current.Start()) { current.Dispose(); unavailable = true; return null; }
                    worker = current;
                    if (disposed) { Stop(); return null; }
                    WorkerStarted?.Invoke(current.Id);
                    try { current.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (System.ComponentModel.Win32Exception) { }
                }
                byte[] request = new byte[64];
                BinaryPrimitives.WriteInt64LittleEndian(request, target.Handle.ToInt64());
                BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(8), target.ProcessId);
                double[] values = [point.X, point.Y, target.Bounds.X, target.Bounds.Y, target.Bounds.Width, target.Bounds.Height];
                for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteDoubleLittleEndian(request.AsSpan(12 + i * 8), values[i]);
                await current.StandardInput.BaseStream.WriteAsync(request, deadline.Token).ConfigureAwait(false);
                await current.StandardInput.BaseStream.FlushAsync(deadline.Token).ConfigureAwait(false);
                byte[] response = new byte[32];
                await current.StandardOutput.BaseStream.ReadExactlyAsync(response, deadline.Token).ConfigureAwait(false);
                var rect = new PixelRect(BinaryPrimitives.ReadDoubleLittleEndian(response), BinaryPrimitives.ReadDoubleLittleEndian(response.AsSpan(8)),
                    BinaryPrimitives.ReadDoubleLittleEndian(response.AsSpan(16)), BinaryPrimitives.ReadDoubleLittleEndian(response.AsSpan(24)));
                if (!double.IsFinite(rect.X) || !double.IsFinite(rect.Y) || !double.IsFinite(rect.Right) || !double.IsFinite(rect.Bottom) ||
                    rect.IsEmpty || !rect.Contains(point) || rect.Intersect(target.Bounds) != rect) return null;
                return rect.RoundOut().Intersect(target.Bounds);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                // A failed provider is not retried on every mouse move during this screenshot.
                unavailable = true; Stop();
                if (disposed) return null;
                if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                diagnostics?.Record("selection", "hover-fallback", new { TimedOut = error is OperationCanceledException }, error: error);
                return null;
            }
        }
        finally { gate.Release(); }
    }
    private void Stop()
    {
        var current = Interlocked.Exchange(ref worker, null);
        if (current is null) return;
        try { if (!current.HasExited) current.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { current.Dispose(); }
    }
    public void Dispose() { disposed = true; Stop(); }
}
