using System;
using System.Drawing;
using System.Windows.Forms;
using Snip.Core;

namespace Snip.Infrastructure;

public sealed class TrayService : ITrayService
{
    private readonly NotifyIcon icon;
    private readonly Icon sheepIcon;
    private readonly ContextMenuStrip menu = new();
    public event EventHandler? CaptureRequested;
    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? PinsRequested;
    public event EventHandler? CaptureCancelRequested;
    public event EventHandler? DiagnosticsRequested;
    private bool capturing;
    public TrayService()
    {
        using var stream = typeof(TrayService).Assembly.GetManifestResourceStream("Snip.Infrastructure.sheep.ico") ?? throw new InvalidOperationException("羊图标资源缺失。");
        sheepIcon = new Icon(stream);
        menu.Items.Add("开始截图  Ctrl+Alt+A", null, (_, _) => CaptureRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("打开主窗口", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("贴图管理", null, (_, _) => PinsRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("导出诊断包…", null, (_, _) => DiagnosticsRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        menu.Opening += (_, _) => { if (capturing) CaptureCancelRequested?.Invoke(this, EventArgs.Empty); };
        icon = new NotifyIcon { Text = "自用截图 · Ctrl+Alt+A", Icon = sheepIcon, ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }
    public void SetCaptureStatus(string? status)
    {
        capturing = status is not null;
        var text = status is null ? "自用截图 · Ctrl+Alt+A" : "自用截图 · " + status;
        icon.Text = text.Length > 63 ? text[..63] : text;
    }
    public void Dispose() { icon.Visible = false; icon.Dispose(); sheepIcon.Dispose(); menu.Dispose(); }
}
