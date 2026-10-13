using System;
using System.Drawing;
using System.Windows.Forms;
using Snip.Core;

namespace Snip.Infrastructure;

public sealed class TrayService : ITrayService
{
    private readonly NotifyIcon icon;
    private readonly ContextMenuStrip menu = new();
    public event EventHandler? CaptureRequested;
    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? PinsRequested;
    public TrayService()
    {
        menu.Items.Add("开始截图  Ctrl+Alt+A", null, (_, _) => CaptureRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("打开主窗口", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("贴图管理", null, (_, _) => PinsRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        icon = new NotifyIcon { Text = "自用截图 · Ctrl+Alt+A", Icon = SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }
    public void Dispose() { icon.Visible = false; icon.Dispose(); menu.Dispose(); }
}
