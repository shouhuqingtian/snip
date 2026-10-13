using System.Windows;
using System.Windows.Controls;

namespace Snip.App.Views;

public sealed class PinManagerWindow : Window
{
    public PinManagerWindow(IReadOnlyList<PinWindow> pins)
    {
        Ui.ApplyWindow(this);
        Title = "自用截图 · 贴图管理"; Width = 560; Height = 460; MinWidth = 500; MinHeight = 330; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new Thickness(24) };
        var header = new StackPanel(); header.Children.Add(Ui.Title("桌面贴图"));
        var description = Ui.Subtitle("恢复鼠标交互后，可以移动、缩放或右键设置贴图。"); description.Margin = new Thickness(0, 6, 0, 20); header.Children.Add(description);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var rows = new StackPanel();
        var empty = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        empty.Children.Add(new UiIcon("pin") { Width = 40, Height = 40, Foreground = Ui.Brush("MutedBrush"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 16) });
        var title = new TextBlock { Text = "还没有桌面贴图", FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center }; empty.Children.Add(title);
        var tip = Ui.Subtitle("截图后，点击工具栏上的图钉即可创建。"); tip.Margin = new Thickness(0, 8, 0, 0); empty.Children.Add(tip);
        var body = new Grid(); body.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); body.Children.Add(empty);
        void UpdateEmpty() => empty.Visibility = rows.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var subscriptions = new List<(PinWindow Pin, EventHandler Handler)>();
        foreach (var pin in pins.ToArray())
        {
            var row = new DockPanel();
            var actions = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(actions, Dock.Right); row.Children.Add(actions);
            var recover = new Button { Content = "恢复交互" }; recover.Click += (_, _) => pin.SetClickThrough(false); actions.Children.Add(recover);
            var close = new Button { Content = "关闭", Foreground = Ui.Brush("DangerBrush") }; Ui.Style(close, "QuietButton"); close.Click += (_, _) => pin.Close(); actions.Children.Add(close);
            row.Children.Add(new TextBlock { Text = pin.Description, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
            var card = Ui.Card(row, new Thickness(14)); card.Margin = new Thickness(0, 0, 0, 10); rows.Children.Add(card);
            EventHandler handler = (_, _) => { rows.Children.Remove(card); UpdateEmpty(); };
            pin.Closed += handler; subscriptions.Add((pin, handler));
        }
        Closed += (_, _) => { foreach (var (pin, handler) in subscriptions) pin.Closed -= handler; };
        UpdateEmpty(); root.Children.Add(body); Content = root;
    }
}
