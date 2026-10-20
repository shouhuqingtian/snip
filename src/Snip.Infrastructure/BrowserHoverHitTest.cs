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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible);

    public static bool IsChromium(IntPtr window)
    {
        var name = new StringBuilder(64); GetClassName(window, name, name.Capacity);
        return name.ToString().StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal);
    }

    public static PixelRect? Find(IntPtr window, PixelRect bounds, PixelPoint point, double scale)
    {
        PixelRect? candidate = null;
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
                // A label belongs to its button/list item/panel. Never turn a glyph fragment
                // into a region, or search other branches after the topmost path was found.
                bool container = role is 9 or 10 or 15 or 16 or 18 or 20 or 22 or 24 or 33 or 35 or 38;
                if (container && (state & 0x18000) == 0 && HoverRegionWorker.IsUsefulRegion(rect, scale) && rect.Contains(point))
                {
                    // A visible web dialog is the intended screenshot window, including its
                    // embedded document. Prefer it over controls/panels inside that dialog.
                    if (role == 18) return rect; // ROLE_SYSTEM_DIALOG
                    candidate ??= rect;
                }
                if (ReferenceEquals(current, root) && childId is int rootId && rootId == 0) break;
                if (childId is int index && index != 0) childId = 0;
                else if (current.accParent is IAccessible parent && !ReferenceEquals(parent, current)) current = parent;
                else break;
            }
            return candidate;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or UnauthorizedAccessException or NotImplementedException or ArgumentException) { }
        return candidate;
    }
}
