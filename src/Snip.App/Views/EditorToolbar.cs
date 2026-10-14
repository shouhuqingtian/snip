using System.Windows;
using System.Windows.Automation;
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
    public event EventHandler? StickerRequested, PinRequested, OcrRequested, TranslateRequested, ShareRequested;
    public event Action<BrowserCaptureMode>? ScrollRequested;
    public EditorToolbar()
    {
        Ui.EnsureTheme();
        var panel = new StackPanel();
        var tools = new WrapPanel();
        panel.Children.Add(tools);
        var converter = new ToolConverter();
        foreach (var (tool, glyph, tip) in new[] {
            (AnnotationTool.Select,"select","选择 / 移动 / 缩放标注"), (AnnotationTool.Rectangle,"rectangle","矩形"), (AnnotationTool.Ellipse,"ellipse","椭圆"),
            (AnnotationTool.Arrow,"arrow","箭头"), (AnnotationTool.Pen,"pen","画笔"), (AnnotationTool.Mosaic,"mosaic","马赛克"), (AnnotationTool.Text,"text","文字（Ctrl+Enter 提交）") })
        {
            var button = new RadioButton { Content = new UiIcon(glyph), ToolTip = tip, Width = 40, Height = 40, Padding = new Thickness(8) };
            AutomationProperties.SetName(button, tip);
            button.SetBinding(RadioButton.IsCheckedProperty, new Binding(nameof(EditorViewModel.Tool)) { Converter = converter, ConverterParameter = tool.ToString(), Mode = BindingMode.TwoWay });
            tools.Children.Add(button);
        }
        Button Add(string glyph, string tip, Action action)
        {
            var button = new Button { Content = new UiIcon(glyph), ToolTip = tip, Width = 40, Height = 40, Padding = new Thickness(8) };
            Ui.Style(button, "IconButton"); AutomationProperties.SetName(button, tip);
            button.Click += (_, _) => action(); tools.Children.Add(button); return button;
        }
        void Divider() => tools.Children.Add(new Border { Width = 1, Height = 22, Margin = new Thickness(7, 9, 7, 9), Background = Ui.Brush("StrokeBrush") });
        Divider();
        Add("sticker", "表情 / 图片贴纸", () => StickerRequested?.Invoke(this, EventArgs.Empty));
        Add("ocr", "离线文字识别", () => OcrRequested?.Invoke(this, EventArgs.Empty));
        Add("translate", "离线中英互译", () => TranslateRequested?.Invoke(this, EventArgs.Empty));
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
        browser = Add("scroll", "Chrome / Edge 网页截图", () => { browserMenu.PlacementTarget = browser; browserMenu.IsOpen = true; });
        Divider();
        var undo = Add("undo", "撤销 Ctrl+Z", () => { }); undo.SetBinding(Button.CommandProperty, new Binding(nameof(EditorViewModel.UndoCommand)));
        var redo = Add("redo", "重做 Ctrl+Y", () => { }); redo.SetBinding(Button.CommandProperty, new Binding(nameof(EditorViewModel.RedoCommand)));
        var save = Add("save", "保存 PNG Ctrl+S", () => { }); save.SetBinding(Button.CommandProperty, new Binding(nameof(EditorViewModel.SaveCommand)));
        Add("pin", "钉在桌面", () => PinRequested?.Invoke(this, EventArgs.Empty));
        Add("share", "Windows 系统分享", () => ShareRequested?.Invoke(this, EventArgs.Empty));
        Add("editor", "独立编辑窗口", () => WindowRequested?.Invoke(this, EventArgs.Empty));
        Add("adjust", "重新调整选区（清除当前标注）", () => SelectionRequested?.Invoke(this, EventArgs.Empty));
        Divider();
        Add("close", "取消截图", () => CancelRequested?.Invoke(this, EventArgs.Empty)).Foreground = Ui.Brush("DangerBrush");
        var complete = Add("check", "复制并完成 Enter", () => CompleteRequested?.Invoke(this, EventArgs.Empty));
        complete.Background = Ui.Brush("AccentBrush"); complete.Foreground = Brushes.White;
        var options = new WrapPanel { Margin = new Thickness(3, 12, 3, 0) };
        panel.Children.Add(options);
        void Label(string text) => options.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6), Foreground = Ui.Brush("MutedBrush"), FontSize = 12 });
        ComboBox Choice(string items, string selected, double width)
        {
            var combo = new ComboBox { Width = width, MinHeight = 32, Padding = new Thickness(8, 5, 8, 5) };
            combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(items)); combo.SetBinding(ComboBox.SelectedItemProperty, new Binding(selected) { Mode = BindingMode.TwoWay });
            options.Children.Add(combo); return combo;
        }
        Label("颜色");
        var palette = new ItemsControl { Margin = new Thickness(2, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        palette.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(EditorViewModel.Colors)));
        var palettePanel = new FrameworkElementFactory(typeof(StackPanel)); palettePanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        palette.ItemsPanel = new ItemsPanelTemplate(palettePanel);
        var swatchButton = new FrameworkElementFactory(typeof(Button));
        swatchButton.SetResourceReference(FrameworkElement.StyleProperty, "QuietButton");
        swatchButton.SetValue(FrameworkElement.WidthProperty, 24.0); swatchButton.SetValue(FrameworkElement.HeightProperty, 32.0);
        swatchButton.SetValue(FrameworkElement.MinHeightProperty, 32.0); swatchButton.SetValue(Control.PaddingProperty, new Thickness(3)); swatchButton.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
        swatchButton.SetBinding(FrameworkElement.ToolTipProperty, new Binding());
        swatchButton.AddHandler(Button.ClickEvent, new RoutedEventHandler((sender, _) => { if (DataContext is EditorViewModel vm && ((Button)sender).DataContext is string value) vm.Color = value; }));
        var dot = new FrameworkElementFactory(typeof(Border)); dot.SetValue(FrameworkElement.WidthProperty, 16.0); dot.SetValue(FrameworkElement.HeightProperty, 16.0);
        dot.SetValue(Border.CornerRadiusProperty, new CornerRadius(8)); dot.SetValue(Border.BorderThicknessProperty, new Thickness(1)); dot.SetValue(Border.BorderBrushProperty, Ui.Brush("StrokeBrush")); dot.SetBinding(Border.BackgroundProperty, new Binding());
        swatchButton.AppendChild(dot); palette.ItemTemplate = new DataTemplate { VisualTree = swatchButton }; options.Children.Add(palette);
        var color = new TextBox { Width = 108, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(3), ToolTip = "#RRGGBB" };
        color.SetBinding(TextBox.TextProperty, new Binding(nameof(EditorViewModel.Color)) { Mode = BindingMode.TwoWay }); options.Children.Add(color);
        Label("线宽"); Choice(nameof(EditorViewModel.StrokeWidths), nameof(EditorViewModel.StrokeWidth), 65);
        Label("字号"); Choice(nameof(EditorViewModel.FontSizes), nameof(EditorViewModel.FontSize), 65);
        Label("马赛克"); Choice(nameof(EditorViewModel.MosaicSizes), nameof(EditorViewModel.MosaicSize), 65);
        var filled = new CheckBox { Content = "填充矩形 / 椭圆", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 4, 4, 4) };
        filled.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(EditorViewModel.Filled)) { Mode = BindingMode.TwoWay }); options.Children.Add(filled);
        Content = Ui.Card(panel, new Thickness(10));
    }
}
