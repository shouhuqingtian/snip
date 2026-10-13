using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace Snip.Infrastructure;

/// <summary>Read-only structural inspection. Unknown text and automation IDs are redacted.</summary>
public static class WeChatDiagnostics
{
    private static readonly HashSet<string> Labels = new(StringComparer.Ordinal)
    { "搜索", "Search", "搜索联系人", "搜索联系人、群聊、公众号", "输入", "输入消息", "消息输入框", "Message", "Type a message", "发送", "Send", "发送图片", "Send image", "发送截图", "发送给", "发送给：", "微信", "WeChat", "确定", "取消", "联系人", "聊天" };
    public static Task<string> ReadAsync() => Task.Run(() =>
    {
        var targets = DesktopWindows.Snapshot().Where(w =>
        { try { return Process.GetProcessById(w.ProcessId).ProcessName is "Weixin" or "WeChat"; } catch { return false; } }).Take(8).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("没有找到可见的微信窗口，请先启动微信。");
        var nodes = new List<object>(); var versions = new List<string>(); var watch = Stopwatch.StartNew();
        foreach (var window in targets)
        {
            var process = Process.GetProcessById(window.ProcessId);
            try { versions.Add(FileVersionInfo.GetVersionInfo(process.MainModule!.FileName).ProductVersion ?? "unknown"); } catch { versions.Add("unavailable"); }
            var queue = new Queue<(AutomationElement Element, int Parent, int Depth)>(); queue.Enqueue((AutomationElement.FromHandle(window.Handle), -1, 0));
            while (queue.Count > 0 && nodes.Count < 1200 && watch.Elapsed < TimeSpan.FromSeconds(15))
            {
                var (element, parent, depth) = queue.Dequeue();
                try
                {
                    var current = element.Current; var bounds = current.BoundingRectangle; int index = nodes.Count;
                    double Finite(double value) => double.IsFinite(value) ? value : 0;
                    nodes.Add(new { Index = index, Parent = parent, Depth = depth, Type = current.ControlType.ProgrammaticName,
                        Class = current.ClassName, Label = Labels.Contains(current.Name) ? current.Name : "[文字隐藏]",
                        current.IsOffscreen, Bounds = new { X = Finite(bounds.X), Y = Finite(bounds.Y), Width = Finite(bounds.Width), Height = Finite(bounds.Height) },
                        Patterns = element.GetSupportedPatterns().Select(p => p.ProgrammaticName).ToArray() });
                    if (depth < 18)
                        for (var child = TreeWalker.ControlViewWalker.GetFirstChild(element); child is not null; child = TreeWalker.ControlViewWalker.GetNextSibling(child))
                            queue.Enqueue((child, index, depth + 1));
                }
                catch (ElementNotAvailableException) { }
            }
        }
        return JsonSerializer.Serialize(new { Versions = versions.Distinct(), UnknownTextRedacted = true, AutomationIdsOmitted = true,
            Truncated = nodes.Count >= 1200 || watch.Elapsed >= TimeSpan.FromSeconds(15), Nodes = nodes }, new JsonSerializerOptions { WriteIndented = true });
    });
}
