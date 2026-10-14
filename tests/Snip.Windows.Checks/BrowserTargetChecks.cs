using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.Windows.Checks;

internal static class BrowserTargetChecks
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr window, uint color, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);

    public static int HoldTransparentOverlay(string[] args)
    {
        int[] bounds = args.Skip(1).Take(4).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        // An external process is necessary: our snapshot intentionally excludes our own windows.
        var window = CreateWindowEx(0x00080000 | 0x00000020 | 0x00000008 | 0x08000000,
            "STATIC", "Public browser target fixture", 0x80000000 | 0x10000000,
            bounds[0], bounds[1], bounds[2], bounds[3], IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero || !SetLayeredWindowAttributes(window, 0, 0, 2)) return 1;
        File.WriteAllText(args[5], "ready");
        System.Windows.Threading.Dispatcher.Run();
        return 0;
    }

    public static void Verify(WindowCaptureInfo browser, PixelRect region, Action captureVisible)
    {
        var ready = Path.Combine(Path.GetTempPath(), "Snip.Browser.Target-" + Guid.NewGuid());
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.ArgumentList.Add("--browser-transparent-overlay");
        foreach (double value in new[] { region.X - 20, region.Y - 20, region.Width + 40, region.Height + 40 })
            start.ArgumentList.Add(((int)value).ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(ready);
        using var overlay = Process.Start(start)!;
        try
        {
            for (int i = 0; i < 100 && !File.Exists(ready) && !overlay.HasExited; i++) Thread.Sleep(50);
            if (!File.Exists(ready)) throw new InvalidOperationException("Transparent overlay fixture did not start.");
            var center = new PixelPoint(region.X + region.Width / 2, region.Y + region.Height / 2);
            var oldTarget = DesktopWindows.Snapshot().First(w => w.Bounds.Contains(center));
            if (oldTarget.ProcessId != overlay.Id) throw new InvalidOperationException("Regression fixture did not reproduce rectangle-based browser misidentification.");
            // Simulate a native caller with DPI virtualization enabled. The physical lookup
            // must still find Chrome and restore the original caller context, even on a pool thread.
            Task.Run(() =>
            {
                var previous = SetThreadDpiAwarenessContext(new IntPtr(-1)); // Unaware
                if (previous == IntPtr.Zero) throw new InvalidOperationException("Cannot set regression thread DPI context.");
                try
                {
                    if (DesktopWindows.AtPhysicalPoint(center)?.Handle != browser.Handle)
                        throw new InvalidOperationException("Physical point selected a covering overlay instead of Chrome.");
                    if (!AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), new IntPtr(-1)))
                        throw new InvalidOperationException("Browser lookup leaked thread DPI awareness.");
                    var layout = new DesktopCaptureService().GetDisplays();
                    if (!layout.Any(d => d.Bounds.Contains(center))) throw new InvalidOperationException("Physical monitor bounds exclude browser point.");
                    if (!AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), new IntPtr(-1)))
                        throw new InvalidOperationException("Monitor enumeration leaked thread DPI awareness.");
                }
                finally { SetThreadDpiAwarenessContext(previous); }
            }).GetAwaiter().GetResult();
            captureVisible();
            Console.WriteLine("PASS physical browser target through external transparent overlay; old rectangle lookup reproduced wrong PID; unaware worker context restored.");
        }
        finally
        {
            if (!overlay.HasExited) { overlay.Kill(); overlay.WaitForExit(5000); }
            File.Delete(ready);
        }
    }
}
