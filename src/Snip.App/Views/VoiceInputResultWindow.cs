using System.Windows;
using System.Windows.Controls;
using Snip.Infrastructure;

namespace Snip.App.Views;

public sealed class VoiceInputResultWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    public VoiceInputResultWindow(string text)
    {
        Ui.ApplyWindow(this); Title = "自用截图 · 语音识别结果"; Width = 700; Height = 520; MinWidth = 420; MinHeight = 300; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new DockPanel { Margin = new Thickness(24) };
        var title = Ui.Title("语音识别结果"); DockPanel.SetDock(title, Dock.Top); panel.Children.Add(title);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom);
        var status = Ui.Subtitle("结果仅在本次运行保留；可复制后粘贴到需要的输入框。"); status.TextWrapping = TextWrapping.Wrap; footer.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var close = new Button { Content = "关闭" }; close.Click += (_, _) => Close(); buttons.Children.Add(close);
        var copy = new Button { Content = "复制文字" }; Ui.Style(copy, "PrimaryButton");
        copy.Click += async (_, _) =>
        {
            copy.IsEnabled = false;
            try { await new TextClipboardService().CopyAsync(text, lifetime.Token); if (!lifetime.IsCancellationRequested) status.Text = "已复制文字。"; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch { status.Text = "复制未完成，结果保留，请稍后重试。"; }
            finally { copy.IsEnabled = true; }
        };
        buttons.Children.Add(copy); footer.Children.Add(buttons); panel.Children.Add(footer);
        panel.Children.Add(new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 14, 0, 0), FontSize = 17 });
        Content = panel;
        Closed += (_, _) => lifetime.Cancel();
    }
}
