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
        Title = "微信控件诊断（只读）"; Width = 800; Height = 620; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new Thickness(16) };
        var notice = new TextBlock { Text = "只读取微信窗口控件类型、类名、位置和支持的交互模式。未知文字（含联系人/聊天内容）隐藏，AutomationId 不输出；不会点击、搜索或发送。请先保持微信窗口可见。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(notice, Dock.Top); root.Children.Add(notice);
        var row = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(row, Dock.Top); root.Children.Add(row);
        var read = new Button { Content = "读取控件摘要" }; var save = new Button { Content = "保存 JSON…", IsEnabled = false }; row.Children.Add(read); row.Children.Add(save);
        var text = new TextBox { IsReadOnly = true, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(text); Content = root;
        read.Click += async (_, _) => { read.IsEnabled = false; try { text.Text = await WeChatDiagnostics.ReadAsync(); save.IsEnabled = true; } catch (Exception e) { text.Text = e.Message; } finally { read.IsEnabled = true; } };
        save.Click += (_, _) => { var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "wechat-controls-redacted.json" }; if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, text.Text); };
    }
}
