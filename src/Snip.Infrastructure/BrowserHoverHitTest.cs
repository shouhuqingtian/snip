using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Accessibility;
using Snip.Core;

namespace Snip.Infrastructure;

/// <summary>Ask the selected Chromium window for its actual hit path, never the overlay or hidden sibling rectangles.</summary>
internal static class BrowserHoverHitTest
{
    [StructLayout(LayoutKind.Sequential)] private struct GuiInfo
    { public uint Size, Flags; public IntPtr Active, Focus, Capture, Menu, Move, Caret; public NativeMethods.NativeRect Rect; }
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible);


    [ComImport, Guid("F8B80ADA-2C44-48D0-89BE-5FF23C9CD875"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAccessibleExNative
    {
        [PreserveSig] int GetObjectForChild(int child, out IAccessibleExNative? result);
        [PreserveSig] int GetIAccessiblePair(out IAccessible? accessible, out int child);
        [PreserveSig] int GetRuntimeId([MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] out int[]? result);
        [PreserveSig] int ConvertReturnedElement(IntPtr provider, out IAccessibleExNative? result);
    }
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAccessibleServices
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object? result);
    }
    internal static int[]? FocusRuntimeId(NativeFocus focus)
    {
        object? accessible = focus.Accessible;
        int child = focus.Child;
        if (child != 0 && focus.Accessible?.get_accChild(child) is IAccessible normalized) { accessible = normalized; child = 0; }
        var extension = accessible as IAccessibleExNative;
        if (extension is null && accessible is IAccessibleServices services)
        {
            var iid = typeof(IAccessibleExNative).GUID; var service = iid;
            if (services.QueryService(ref service, ref iid, out var result) < 0)
            { service = typeof(IAccessible).GUID; services.QueryService(ref service, ref iid, out result); }
            extension = result as IAccessibleExNative;
        }
        if (child != 0 && extension is not null)
        {
            if (extension.GetObjectForChild(child, out var childExtension) < 0) return null;
            extension = childExtension;
        }
        return extension is not null && extension.GetRuntimeId(out var runtime) >= 0 && runtime is { Length: >= 1 and <= 60 } ? runtime : null;
    }

    public static bool IsChromium(IntPtr window)
    {
        var name = new StringBuilder(64); GetClassName(window, name, name.Capacity);
        return name.ToString().StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal);
    }

    internal sealed record NativeFocus(IntPtr Window, IAccessible? Accessible, int Child, int? Role, int? State);
    internal static NativeFocus WarmFocusedAccessibility(IntPtr window)
    {
        // Electron may expose only its window pane to a new UIA client until a
        // native accessibility client asks for OBJID_CLIENT. This requests only
        // the selected foreground window and its focus, never page text.
        var info = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() };
        var focused = GetGUIThreadInfo(0, ref info) && IsChild(window, info.Focus) ? info.Focus : window;
        var iid = typeof(IAccessible).GUID;
        if (AccessibleObjectFromWindow(focused, unchecked((uint)-4), ref iid, out var root) >= 0 && root is not null)
        {
            object child = 0;
            for (int depth = 0; depth < 8; depth++)
            {
                var next = root.accFocus;
                if (next is IAccessible accessible && !ReferenceEquals(root, accessible)) { root = accessible; continue; }
                if (next is int id) child = id;
                break;
            }
            return new(focused, root, (int)child, root.get_accRole(child) as int?, root.get_accState(child) as int?);
        }
        return new(focused, null, 0, null, null);
    }

    public static PixelRect? Find(IntPtr window, PixelRect bounds, PixelPoint point, double scale)
    {
        try
        {
            var iid = typeof(IAccessible).GUID;
            if (AccessibleObjectFromWindow(window, unchecked((uint)-4), ref iid, out var root) < 0 || root is null) return null; // OBJID_CLIENT
            var clock = Stopwatch.StartNew();
            IAccessible current = root;
            object childId = 0;
            for (int depth = 0; depth < 24 && clock.ElapsedMilliseconds < 300; depth++)
            {
                // Provider-scoped hit testing follows CSS stacking and iframe hit paths even
                // while our frozen selection window covers the desktop. FromPoint would hit us.
                var hit = current.accHitTest((int)point.X, (int)point.Y);
                if (hit is IAccessible next && !ReferenceEquals(next, current)) { current = next; continue; }
                if (hit is int id) childId = id;
                break;
            }
            for (int depth = 0; depth < 24 && clock.ElapsedMilliseconds < 300; depth++)
            {
                int role = current.get_accRole(childId) is int value ? value : 0;
                int state = current.get_accState(childId) is int flags ? flags : 0;
                current.accLocation(out int x, out int y, out int width, out int height, childId);
                var rect = new PixelRect(x, y, width, height).Intersect(bounds).RoundOut();
                // Ordinary document/panel bounds may extend beyond the visible page after
                // scrolling. Only semantic dialogs refine the native page viewport; never
                // select a column, toolbar or scrolled document rectangle as the page.
                if (role == 18 && (state & 0x18000) == 0 && HoverRegionWorker.IsUsefulRegion(rect, scale) && rect.Contains(point))
                    return rect; // ROLE_SYSTEM_DIALOG
                if (ReferenceEquals(current, root) && childId is int rootId && rootId == 0) break;
                if (childId is int index && index != 0) childId = 0;
                else if (current.accParent is IAccessible parent && !ReferenceEquals(parent, current)) current = parent;
                else break;
            }
            return null;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or UnauthorizedAccessException or NotImplementedException or ArgumentException) { }
        return null;
    }
}
