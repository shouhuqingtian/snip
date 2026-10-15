using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using Snip.Core;

namespace Snip.Infrastructure;

public sealed class TextClipboardService : ITextClipboardService
{
    public const int ClipboardBusy = unchecked((int)0x800401D0);

    public async Task CopyAsync(string text, CancellationToken cancellationToken, IProgress<int>? retries = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA || SynchronizationContext.Current is null)
            throw new InvalidOperationException("复制文字需要在桌面界面线程执行。");
        cancellationToken.ThrowIfCancellationRequested();
        // Allocate before touching existing contents. Immediate CF_UNICODETEXT avoids OLE's
        // synchronous data-object/flush RPC, which can block even with framework retries disabled.
        IntPtr memory = GlobalAlloc(0x0042, checked((nuint)((text.Length + 1L) * 2))); // MOVEABLE | ZEROINIT
        if (memory == IntPtr.Zero) throw new OutOfMemoryException();
        try
        {
            IntPtr pointer = GlobalLock(memory);
            if (pointer == IntPtr.Zero) throw new Win32Exception();
            try { Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length); Marshal.WriteInt16(pointer, text.Length * 2, 0); }
            finally { GlobalUnlock(memory); }
            using var owner = new HwndSource(new HwndSourceParameters("Snip.TextClipboard")
                { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
            for (int attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryPublish(owner.Handle, ref memory, cancellationToken)) return;
                if (attempt >= 7) throw new ExternalException("剪贴板暂时被其他程序占用。", ClipboardBusy);
                retries?.Report(attempt + 1);
                await Task.Delay(100 + attempt * 50, cancellationToken);
            }
        }
        finally { if (memory != IntPtr.Zero) GlobalFree(memory); }
    }

    private static bool TryPublish(IntPtr owner, ref IntPtr memory, CancellationToken token)
    {
        if (!OpenClipboard(owner)) return false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!EmptyClipboard()) throw new Win32Exception();
            if (SetClipboardData(13, memory) == IntPtr.Zero) throw new Win32Exception(); // CF_UNICODETEXT
            memory = IntPtr.Zero; // Windows owns the movable allocation only after success.
            return true;
        }
        finally { CloseClipboard(); }
    }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}
