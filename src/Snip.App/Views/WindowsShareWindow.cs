using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.App.Views;

public sealed class WindowsShareWindow : Window
{
    private readonly CancellationTokenSource cancellation = new();
    private IWindowsShareSession? session;
    private bool closed, busy;
    public WindowsShareWindow(BitmapSource image, IImageExportService export, DiagnosticJournal? diagnostics = null,
        bool autoOpen = true, Func<IntPtr, IWindowsShareSession>? createSession = null)
    {
        Ui.ApplyWindow(this);
        Title = "自用截图 · Windows 系统分享"; Width = 720; Height = 650; MinWidth = 540; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new Thickness(24) };
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        heading.Children.Add(Ui.Title("分享截图"));
        heading.Children.Add(Ui.Subtitle("从 Windows 分享面板选择支持接收图片的应用或附近设备。可用目标由系统和已安装应用决定。"));
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var status = Ui.Subtitle("截图已保留，可以分享、复制或保存。"); status.Name = "ShareStatus"; status.Margin = new Thickness(0, 14, 0, 8); bottom.Children.Add(status);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; bottom.Children.Add(actions);
        var copy = new Button { Content = "复制图片" }; var save = new Button { Content = "保存 PNG" };
        var share = new Button { Content = "打开系统分享", Name = "SystemShareButton" }; Ui.Style(share, "PrimaryButton");
        actions.Children.Add(copy); actions.Children.Add(save); actions.Children.Add(share);
        root.Children.Add(new Border { Background = Ui.Brush("CanvasBrush"), CornerRadius = new CornerRadius(12), Padding = new Thickness(12), Margin = new Thickness(0, 18, 0, 0), Child = new Image { Source = image, Stretch = Stretch.Uniform } }); Content = root;
        async Task OpenAsync()
        {
            if (closed || busy) return;
            busy = true; share.IsEnabled = false;
            using var operation = diagnostics?.Begin("system-share", new { image.PixelWidth, image.PixelHeight });
            try
            {
                status.Text = "正在准备截图…";
                if (session is null)
                {
                    session = (createSession ?? (handle => new WindowsShareSession(handle)))(new WindowInteropHelper(this).EnsureHandle());
                    await session.PrepareAsync(image, cancellation.Token);
                }
                cancellation.Token.ThrowIfCancellationRequested(); if (closed) return;
                session.Show(); operation?.Complete();
                status.Text = "系统分享面板已打开。请选择目标；关闭面板后仍可重试、复制或保存。";
            }
            catch (OperationCanceledException) { operation?.Cancel("window-closed"); }
            catch (Exception error)
            {
                operation?.Fail(error); session?.Dispose(); session = null;
                if (!closed) status.Text = "系统分享未打开：" + error.Message + " 可复制图片或保存 PNG 后手动发送。";
            }
            finally { busy = false; if (!closed) share.IsEnabled = true; }
        }
        share.Click += async (_, _) => await OpenAsync();
        copy.Click += async (_, _) =>
        {
            if (closed || !copy.IsEnabled) return; copy.IsEnabled = false;
            try { await export.CopyAsync(image); if (!closed) status.Text = "图片已复制，可以粘贴到聊天窗口。"; }
            catch (Exception error) { if (!closed) status.Text = "复制失败：" + error.Message; }
            finally { if (!closed) copy.IsEnabled = true; }
        };
        save.Click += async (_, _) =>
        {
            if (closed || !save.IsEnabled) return;
            var path = new Snip.App.Services.EditorDialogs(() => this).ChoosePngPath(); if (path is null) return;
            save.IsEnabled = false;
            try { await Task.Run(() => export.SavePng(image, path)); if (!closed) status.Text = "PNG 已保存。"; }
            catch (Exception error) { if (!closed) status.Text = "保存失败：" + error.Message; }
            finally { if (!closed) save.IsEnabled = true; }
        };
        Loaded += async (_, _) => { if (autoOpen) await OpenAsync(); };
        Closed += (_, _) => { closed = true; cancellation.Cancel(); session?.Dispose(); cancellation.Dispose(); };
    }
}
