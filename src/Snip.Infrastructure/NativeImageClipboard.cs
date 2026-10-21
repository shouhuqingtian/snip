using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace Snip.Infrastructure;

/// <summary>Immediate CF_DIBV5 data survives process exit without OLE delayed rendering or Flush.</summary>
internal static class NativeImageClipboard
{
    private static readonly SemaphoreSlim gate = new(1, 1);
    private static readonly int[] retryDelays = [80, 100, 140, 180, 220, 260, 300];
    private const uint DibV5 = 17;

    internal static async Task CopyAsync(BitmapSource image, CancellationToken token, IProgress<ImageCopyProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA || SynchronizationContext.Current is null)
            throw new InvalidOperationException("复制图片需要在桌面界面线程执行。");
        token.ThrowIfCancellationRequested();
        var snapshot = image.IsFrozen ? image : image.CloneCurrentValue();
        if (!snapshot.IsFrozen) snapshot.Freeze();
        using (var queueDeadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            queueDeadline.CancelAfter(TimeSpan.FromMilliseconds(1600));
            try { await gate.WaitAsync(queueDeadline.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw Busy(); }
        }
        try
        {
            token.ThrowIfCancellationRequested(); progress?.Report(new("image-preparing"));
            using var dib = await Task.Run(() => Prepare(snapshot, token), token);
            token.ThrowIfCancellationRequested(); progress?.Report(new("image-ready", Bytes: dib.Bytes));
            using var owner = new HwndSource(new HwndSourceParameters("Snip.ImageClipboard")
                { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
            for (int attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (OpenClipboard(owner.Handle))
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        if (!EmptyClipboard()) throw new Win32Exception();
                        if (SetClipboardData(DibV5, dib.DangerousGetHandle()) == IntPtr.Zero) throw new Win32Exception();
                        dib.SetHandleAsInvalid(); // Windows owns the movable allocation after success.
                    }
                    finally { CloseClipboard(); }
                    progress?.Report(new("clipboard-published", attempt + 1));
                    return;
                }
                int? blockingProcess = null;
                var holder = GetOpenClipboardWindow();
                if (holder != IntPtr.Zero && GetWindowThreadProcessId(holder, out int pid) != 0 && pid > 0) blockingProcess = pid;
                progress?.Report(new("clipboard-busy", attempt + 1, blockingProcess));
                if (attempt >= retryDelays.Length) throw Busy();
                await Task.Delay(retryDelays[attempt], token);
            }
        }
        finally { gate.Release(); }
    }

    private static ExternalException Busy() => new("剪贴板暂时被其他程序占用，截图和标注已保留，请稍后重试。", TextClipboardService.ClipboardBusy);

    private static DibMemory Prepare(BitmapSource source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int width = source.PixelWidth, height = source.PixelHeight;
        int stride = checked(width * 4), bytes = checked(stride * height);
        var memory = new DibMemory(checked(124 + bytes));
        try
        {
            var pointer = GlobalLock(memory.DangerousGetHandle());
            if (pointer == IntPtr.Zero) throw new Win32Exception();
            try
            {
                Span<byte> header = stackalloc byte[124]; header.Clear();
                BinaryPrimitives.WriteInt32LittleEndian(header, 124);
                BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
                BinaryPrimitives.WriteInt32LittleEndian(header[8..], -height); // top-down rows
                BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1);
                BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 32);
                BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 3); // BI_BITFIELDS
                BinaryPrimitives.WriteInt32LittleEndian(header[20..], bytes);
                BinaryPrimitives.WriteInt32LittleEndian(header[24..], (int)Math.Round(source.DpiX * 100 / 2.54));
                BinaryPrimitives.WriteInt32LittleEndian(header[28..], (int)Math.Round(source.DpiY * 100 / 2.54));
                BinaryPrimitives.WriteUInt32LittleEndian(header[40..], 0x00ff0000);
                BinaryPrimitives.WriteUInt32LittleEndian(header[44..], 0x0000ff00);
                BinaryPrimitives.WriteUInt32LittleEndian(header[48..], 0x000000ff);
                BinaryPrimitives.WriteUInt32LittleEndian(header[52..], 0xff000000);
                BinaryPrimitives.WriteUInt32LittleEndian(header[56..], 0x73524742); // LCS_sRGB
                BinaryPrimitives.WriteUInt32LittleEndian(header[108..], 4); // LCS_GM_IMAGES
                Marshal.Copy(header.ToArray(), 0, pointer, header.Length);
                BitmapSource pixels = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                // Copy directly into native clipboard storage; no full-size managed buffer or PNG encoding.
                pixels.CopyPixels(Int32Rect.Empty, IntPtr.Add(pointer, 124), bytes, stride);
                token.ThrowIfCancellationRequested();
            }
            finally { GlobalUnlock(memory.DangerousGetHandle()); }
            return memory;
        }
        catch { memory.Dispose(); throw; }
    }

    private sealed class DibMemory : SafeHandleZeroOrMinusOneIsInvalid
    {
        public int Bytes { get; }
        public DibMemory(int bytes) : base(true)
        {
            Bytes = bytes; SetHandle(GlobalAlloc(0x0042, (nuint)bytes));
            if (IsInvalid) throw new OutOfMemoryException();
        }
        protected override bool ReleaseHandle() => GlobalFree(handle) == IntPtr.Zero;
    }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetOpenClipboardWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}
