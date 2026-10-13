using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Snip.Infrastructure;

namespace Snip.App.Views;

public sealed class WeChatDiagnosticsWindow : Window
{
    public WeChatDiagnosticsWindow()
    {
        Ui.EnsureTheme();
        Title = "微信控件诊断（只读）"; Width = 860; Height = 680; MinWidth = 600; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new Thickness(24) };
        var header = new StackPanel(); header.Children.Add(Ui.Title("微信控件诊断"));
        var notice = Ui.Subtitle("只读取控件结构，不操作微信。联系人和聊天内容会隐藏；请先保持微信窗口可见。"); notice.Margin = new Thickness(0, 6, 0, 16); header.Children.Add(notice);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) }; DockPanel.SetDock(row, Dock.Top); root.Children.Add(row);
        var read = new Button { Content = "读取控件摘要" }; Ui.Style(read, "PrimaryButton"); var save = new Button { Content = "保存 JSON…", IsEnabled = false }; row.Children.Add(read); row.Children.Add(save);
        var text = new TextBox { IsReadOnly = true, AcceptsReturn = true, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(14), FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 12, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(Ui.Card(text, new Thickness(12))); Content = root;
        read.Click += async (_, _) => { read.IsEnabled = false; try { text.Text = await WeChatDiagnostics.ReadAsync(); save.IsEnabled = true; } catch (Exception e) { text.Text = e.Message; } finally { read.IsEnabled = true; } };
        save.Click += (_, _) => { var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "wechat-controls-redacted.json" }; if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, text.Text); };
    }
}
