using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Snip.App.ViewModels;
using Snip.Core;

namespace Snip.App.Views;

/// <summary>The same toolbar is hosted by the screen selection and standalone editor.</summary>
public sealed class EditorToolbar : UserControl
{
    public event EventHandler? CompleteRequested, CancelRequested, WindowRequested, SelectionRequested;
    public event EventHandler? StickerRequested, PinRequested, OcrRequested, TranslateRequested, WeChatRequested;
    public event Action<BrowserCaptureMode>? ScrollRequested;
    public EditorToolbar()
    {
        var panel = new StackPanel();
        var tools = new WrapPanel();
        panel.Children.Add(tools);
        var converter = new ToolConverter();
        foreach (var (tool, glyph, tip) in new[] {
            (AnnotationTool.Select,"↖","选择 / 移动 / 缩放标注"), (AnnotationTool.Rectangle,"□","矩形"), (AnnotationTool.Ellipse,"○","椭圆"),
            (AnnotationTool.Arrow,"↗","箭头"), (AnnotationTool.Pen,"✎","画笔"), (AnnotationTool.Mosaic,"▦","马赛克"), (AnnotationTool.Text,"T","文字（Ctrl+Enter 提交）") })
        {
            var button = new RadioButton { Content = glyph, ToolTip = tip, FontSize = 23, Width = 42, Height = 42, Padding = new Thickness(0) };
            button.SetBinding(RadioButton.IsCheckedProperty, new Binding(nameof(EditorViewModel.Tool)) { Converter = converter, ConverterParameter = tool.ToString(), Mode = BindingMode.TwoWay });
            tools.Children.Add(button);
        }
        Button Add(string glyph, string tip, Action action)
        {
            var button = new Button { Content = glyph, ToolTip = tip, FontSize = 23, Width = 42, Height = 42, Padding = new Thickness(0) };
            button.Click += (_, _) => action(); tools.Children.Add(button); return button;
        }
        Add("☺", "表情 / 图片贴纸", () => StickerRequested?.Invoke(this, EventArgs.Empty));
        Add("文", "离线文字识别", () => OcrRequested?.Invoke(this, EventArgs.Empty));
        Add("译", "离线中英互译", () => TranslateRequested?.Invoke(this, EventArgs.Empty));
        var browserMenu = new ContextMenu();
        foreach (var (mode, title) in new[] {
            (BrowserCaptureMode.EntirePage, "捕捉整个网页（自动从顶部到页尾）"),
            (BrowserCaptureMode.VisiblePage, "捕捉可见网页（不滚动）"),
            (BrowserCaptureMode.SelectedRegion, "捕捉选定区域（不滚动）"),
            (BrowserCaptureMode.SelectedRegionScroll, "选定区域长截图（从当前位置向下）") })
        {
            var item = new MenuItem { Header = title }; item.Click += (_, _) => ScrollRequested?.Invoke(mode); browserMenu.Items.Add(item);
        }
        Button? browser = null;
        browser = Add("↕", "Chrome / Edge 网页截图", () => { browserMenu.PlacementTarget = browser; browserMenu.IsOpen = true; });
        var undo = Add("↶", "撤销 Ctrl+Z", () => { }); undo.SetBinding(Button.CommandProperty, new Binding(nameof(EditorViewModel.UndoCommand)));
        var redo = Add("↷", "重做 Ctrl+Y", () => { }); redo.SetBinding(Button.CommandProperty, new Binding(nameof(EditorViewModel.RedoCommand)));
        var save = Add("↓", "保存 PNG Ctrl+S", () => { }); save.SetBinding(Button.CommandProperty, new Binding(nameof(EditorViewModel.SaveCommand)));
        Add("⌖", "钉在桌面", () => PinRequested?.Invoke(this, EventArgs.Empty));
        Add("➦", "选择微信联系人并发送", () => WeChatRequested?.Invoke(this, EventArgs.Empty));
        Add("▣", "独立编辑窗口", () => WindowRequested?.Invoke(this, EventArgs.Empty));
        Add("⌗", "重新调整选区（清除当前标注）", () => SelectionRequested?.Invoke(this, EventArgs.Empty));
        Add("×", "取消截图", () => CancelRequested?.Invoke(this, EventArgs.Empty)).Foreground = Brushes.IndianRed;
        Add("✓", "复制并完成 Enter", () => CompleteRequested?.Invoke(this, EventArgs.Empty)).Foreground = Brushes.SeaGreen;
        var options = new WrapPanel { Margin = new Thickness(6, 5, 6, 0) };
        panel.Children.Add(options);
        void Label(string text) => options.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5) });
        ComboBox Choice(string items, string selected, double width)
        {
            var combo = new ComboBox { Width = width };
            combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(items)); combo.SetBinding(ComboBox.SelectedItemProperty, new Binding(selected) { Mode = BindingMode.TwoWay });
            options.Children.Add(combo); return combo;
        }
        Label("颜色"); Choice(nameof(EditorViewModel.Colors), nameof(EditorViewModel.Color), 100);
        var color = new TextBox { Width = 85, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(3), ToolTip = "#RRGGBB" };
        color.SetBinding(TextBox.TextProperty, new Binding(nameof(EditorViewModel.Color)) { Mode = BindingMode.TwoWay }); options.Children.Add(color);
        Label("线宽"); Choice(nameof(EditorViewModel.StrokeWidths), nameof(EditorViewModel.StrokeWidth), 55);
        Label("字号"); Choice(nameof(EditorViewModel.FontSizes), nameof(EditorViewModel.FontSize), 55);
        Label("马赛克"); Choice(nameof(EditorViewModel.MosaicSizes), nameof(EditorViewModel.MosaicSize), 55);
        var filled = new CheckBox { Content = "填充矩形 / 椭圆", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 4, 4, 4) };
        filled.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(EditorViewModel.Filled)) { Mode = BindingMode.TwoWay }); options.Children.Add(filled);
        Content = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(10), Padding = new Thickness(8), BorderBrush = new SolidColorBrush(Color.FromRgb(216, 225, 234)), BorderThickness = new Thickness(1), Child = panel };
    }
}
