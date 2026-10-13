using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Text.RegularExpressions;

namespace Snip.Infrastructure;

public sealed record WeChatContact(string DisplayName, AutomationElement Element);

/// <summary>Version-scoped UI Automation adapter. No private protocol or contact database access.</summary>
public sealed class WeChatAutomation
{
    private const string SupportedVersion = "4.1.15.12";
    private WindowCaptureInfo? target;
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
    private AutomationElement Root()
    {
        if (target is null)
        {
            target = DesktopWindows.Snapshot().FirstOrDefault(w =>
            {
                try { return Process.GetProcessById(w.ProcessId).ProcessName is "Weixin" or "WeChat"; } catch { return false; }
            });
            if (target is null) throw new InvalidOperationException("请先启动并登录 Windows 微信。");
            var p = Process.GetProcessById(target.ProcessId);
            var info = FileVersionInfo.GetVersionInfo(p.MainModule?.FileName ?? throw new InvalidOperationException("无法读取微信版本。"));
            if (!Regex.IsMatch(info.ProductVersion ?? "", @"\b4\.1\.15\.12\b") && !Regex.IsMatch(info.FileVersion ?? "", @"\b4\.1\.15\.12\b"))
            { target = null; throw new InvalidOperationException("当前适配目标为微信 4.1.15.12，请核对版本。未匹配版本不执行发送。"); }
        }
        return AutomationElement.FromHandle(target.Handle);
    }
    public async Task<IReadOnlyList<WeChatContact>> SearchAsync(string query, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 80) throw new ArgumentException("请输入 1–80 字的联系人或群名称。");
        return await Task.Run(async () =>
        {
            var root = Root(); if (!SetForegroundWindow(target!.Handle)) throw new InvalidOperationException("无法激活微信窗口。");
            var edits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Cast<AutomationElement>();
            var search = edits.FirstOrDefault(e => new[] { "搜索", "Search", "搜索联系人", "搜索联系人、群聊、公众号", "Search Contacts" }.Contains(e.Current.Name));
            if (search is null || !search.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
                throw new InvalidOperationException("此微信窗口未提供可用的联系人搜索控件，无法安全自动选择；需要 Windows 实机适配。");
            ((ValuePattern)value).SetValue(query); await Task.Delay(700, token);
            var items = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Cast<AutomationElement>();
            var contacts = items.Where(e => !e.Current.IsOffscreen && e.Current.ProcessId == target.ProcessId && e.Current.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(e => new WeChatContact(e.Current.Name, e)).Take(30).ToArray();
            if (contacts.Length == 0) throw new InvalidOperationException("未找到可核实的搜索结果。请检查登录状态、搜索词及微信辅助功能支持。");
            if (contacts.GroupBy(c => c.DisplayName, StringComparer.Ordinal).Any(g => g.Count() > 1))
                throw new InvalidOperationException("搜索结果包含同名目标，无法唯一核实。请使用唯一备注或完整微信号重试。");
            return (IReadOnlyList<WeChatContact>)contacts;
        }, token);
    }
    public Task PrepareConversationAsync(WeChatContact contact, CancellationToken token) => Task.Run(async () =>
    {
        var root = Root(); SetForegroundWindow(target!.Handle);
        if (contact.Element.Current.ProcessId != target.ProcessId || contact.Element.Current.IsOffscreen) throw new InvalidOperationException("联系人搜索结果已变化，请重新搜索。");
        if (contact.Element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) ((InvokePattern)invoke).Invoke();
        else if (contact.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection)) ((SelectionItemPattern)selection).Select();
        else throw new InvalidOperationException("联系人控件不能安全打开会话。");
        await Task.Delay(500, token); VerifyConversation(root, contact);
    }, token);
    private static void VerifyConversation(AutomationElement root, WeChatContact contact)
    {
        var edits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Cast<AutomationElement>();
        var composer = edits.FirstOrDefault(e => !e.Current.IsOffscreen && new[] { "输入消息", "消息输入框", "输入", "Message", "Type a message" }.Contains(e.Current.Name));
        if (composer is null) throw new InvalidOperationException("无法核实微信会话输入区，未执行发送。");
        var region = composer.Current.BoundingRectangle; var window = root.Current.BoundingRectangle;
        bool InList(AutomationElement element)
        {
            for (var p = TreeWalker.ControlViewWalker.GetParent(element); p is not null && !p.Equals(root); p = TreeWalker.ControlViewWalker.GetParent(p))
                if (p.Current.ControlType is var type && (type == ControlType.List || type == ControlType.ListItem || type == ControlType.Tree || type == ControlType.TreeItem)) return true;
            return false;
        }
        var titles = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>();
        var headers = titles.Where(e => !e.Current.IsOffscreen && e.Current.Name == contact.DisplayName && !InList(e))
            .Where(e => e.Current.BoundingRectangle.Left >= region.Left - 12 && e.Current.BoundingRectangle.Right <= region.Right + 12
                && e.Current.BoundingRectangle.Top <= window.Top + window.Height * .25 && e.Current.BoundingRectangle.Bottom < region.Top).ToArray();
        if (headers.Length != 1)
            throw new InvalidOperationException("会话标题与选中联系人不一致，未执行发送。");
    }
    public Task FocusComposerAsync(WeChatContact contact, CancellationToken token) => Task.Run(() =>
    {
        var root = Root(); if (!SetForegroundWindow(target!.Handle)) throw new InvalidOperationException("无法激活微信。");
        VerifyConversation(root, contact);
        var edits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Cast<AutomationElement>();
        var input = edits.FirstOrDefault(e => !e.Current.IsOffscreen && new[] { "输入消息", "消息输入框", "输入", "Message", "Type a message" }.Contains(e.Current.Name));
        if (input is null || !input.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) throw new InvalidOperationException("无法核实微信消息输入框，未执行发送。");
        if (!string.IsNullOrWhiteSpace(((ValuePattern)value).Current.Value)) throw new InvalidOperationException("微信输入框存在未发送文字，请先处理后重试。");
        input.SetFocus();
    }, token);
    public Task PasteAndSendAsync(WeChatContact contact, CancellationToken token) => Task.Run(async () =>
    {
        var root = Root(); VerifyConversation(root, contact);
        if (GetForegroundWindow() != target!.Handle || AutomationElement.FocusedElement.Current.ProcessId != target.ProcessId)
            throw new InvalidOperationException("微信焦点已变化，未发送。");
        var keys = new[] { Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true) };
        if (SendInput((uint)keys.Length, keys, Marshal.SizeOf<Input>()) != keys.Length) throw new InvalidOperationException("无法向微信粘贴截图。");
        await Task.Delay(600, token);
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, target.ProcessId)).Cast<AutomationElement>();
        var preview = windows.SingleOrDefault(w => w.Current.Name is "发送图片" or "Send image" or "发送截图");
        if (preview is null) throw new InvalidOperationException("未找到可核实的微信图片预览窗口，未点击发送；请到微信检查已粘贴的截图。");
        VerifyConversation(root, contact);
        var send = preview.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>()
            .Where(e => !e.Current.IsOffscreen && e.Current.IsEnabled && e.Current.Name is "发送" or "Send").ToArray();
        if (send.Length != 1 || !send[0].TryGetCurrentPattern(InvokePattern.Pattern, out var action)) throw new InvalidOperationException("微信发送按钮无法唯一核实，未点击发送。");
        token.ThrowIfCancellationRequested(); ((InvokePattern)action).Invoke();
        // The native UI action completed; this does not assert server delivery/read receipt.
    }, token);
    private static Input Key(ushort key, bool up) => new() { Type = 1, Union = new InputUnion { Keyboard = new KeyboardInput { Vk = key, Flags = up ? 2u : 0u } } };
}
