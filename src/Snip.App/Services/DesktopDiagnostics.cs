using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.IO;
using Snip.Infrastructure;

namespace Snip.App.Services;

public static class DesktopDiagnostics
{
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    public static string Version => typeof(DesktopDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    public static object ResourceUsage()
    {
        try
        {
            using var process = Process.GetCurrentProcess(); process.Refresh();
            return new { WorkingSetBytes = process.WorkingSet64, PrivateMemoryBytes = process.PrivateMemorySize64,
                ManagedHeapBytes = GC.GetTotalMemory(false), ProcessorSeconds = process.TotalProcessorTime.TotalSeconds,
                Gen0Collections = GC.CollectionCount(0), Gen1Collections = GC.CollectionCount(1), Gen2Collections = GC.CollectionCount(2) };
        }
        catch { return new { Unavailable = true }; }
    }
    public static object EnvironmentInfo(IDesktopCaptureService capture)
    {
        object? displays = null; string? displayError = null;
        try { displays = capture.GetDisplays().Select(d =>
            {
                var monitor = MonitorFromPoint(new NativePoint { X = (int)d.Bounds.X + 1, Y = (int)d.Bounds.Y + 1 }, 2);
                int result = GetDpiForMonitor(monitor, 0, out uint x, out uint y);
                return new { d.Bounds, DpiX = result == 0 ? (uint?)x : null, DpiY = result == 0 ? (uint?)y : null, d.Primary };
            }).ToArray(); }
        catch (Exception e) { displayError = e.GetType().Name; }
        string? Browser(string vendor, string executable)
        {
            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData })
            {
                var path = Path.Combine(Environment.GetFolderPath(folder), vendor, "Application", executable);
                try { if (File.Exists(path)) return FileVersionInfo.GetVersionInfo(path).ProductVersion; } catch { }
            }
            return null;
        }
        return new { AppVersion = Version, OperatingSystem = RuntimeInformation.OSDescription, Runtime = RuntimeInformation.FrameworkDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
            Displays = displays, DisplayError = displayError, Resources = ResourceUsage(), ChromeVersion = Browser("Google/Chrome", "chrome.exe"), EdgeVersion = Browser("Microsoft/Edge", "msedge.exe") };
    }
}
