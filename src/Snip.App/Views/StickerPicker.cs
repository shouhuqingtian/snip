using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Snip.App.Views;

public sealed class StickerPicker : Window
{
    public byte[]? Png { get; private set; }
    public double ImageWidth { get; private set; }
    public double ImageHeight { get; private set; }
    public StickerPicker()
    {
        Ui.EnsureTheme();
        Title = "表情与图片贴纸"; Width = 460; Height = 440; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(Ui.Title("让画面更有趣"));
        var description = Ui.Subtitle("选择一个表情，或导入自己的图片贴纸。"); description.Margin = new Thickness(0, 6, 0, 16); root.Children.Add(description);
        var grid = new UniformGrid { Columns = 4, Rows = 2 }; root.Children.Add(Ui.Card(grid, new Thickness(10)));
        for (int i = 0; i < 8; i++)
        {
            var bitmap = Face(i);
            var button = new Button { Content = new Image { Source = bitmap, Width = 54, Height = 54 }, Padding = new Thickness(12), ToolTip = "插入表情贴纸" }; Ui.Style(button, "QuietButton");
            button.Click += (_, _) => Select(bitmap); grid.Children.Add(button);
        }
        var import = new Button { Content = "导入图片贴纸…", Margin = new Thickness(0, 18, 0, 0) }; Ui.Style(import, "PrimaryButton");
        import.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "图片 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg", Title = "导入贴纸" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                if (new FileInfo(dialog.FileName).Length > 20 * 1024 * 1024) throw new InvalidOperationException("请选择 20 MB 以内的图片。");
                using var metadata = File.OpenRead(dialog.FileName);
                var frame = BitmapDecoder.Create(metadata, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand).Frames[0];
                if ((long)frame.PixelWidth * frame.PixelHeight > 40_000_000) throw new InvalidOperationException("贴纸像素总量不能超过 4000 万。");
                double scale = Math.Min(1, Math.Min(2048.0 / frame.PixelWidth, 2048.0 / frame.PixelHeight));
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = Math.Max(1, (int)(frame.PixelWidth * scale)); image.DecodePixelHeight = Math.Max(1, (int)(frame.PixelHeight * scale));
                image.UriSource = new Uri(dialog.FileName); image.EndInit(); image.Freeze(); Select(image);
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, "无法导入贴纸", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        root.Children.Add(import);
        var formats = Ui.Subtitle("支持 PNG、JPEG · 单张图片不超过 20 MB"); formats.FontSize = 12; formats.Margin = new Thickness(0, 10, 0, 0); root.Children.Add(formats); Content = root;
    }
    private void Select(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = new MemoryStream(); encoder.Save(stream);
        Png = stream.ToArray(); ImageWidth = bitmap.PixelWidth; ImageHeight = bitmap.PixelHeight; DialogResult = true;
    }
    private static BitmapSource Face(int kind)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawEllipse(Brushes.Gold, new System.Windows.Media.Pen(Brushes.DarkGoldenrod, 3), new Point(64, 64), 58, 58);
            var pen = new System.Windows.Media.Pen(Brushes.SaddleBrown, 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (kind == 4) dc.DrawLine(pen, new(35, 46), new(48, 46)); else dc.DrawEllipse(Brushes.SaddleBrown, null, new(42, 46), 5, 7);
            dc.DrawEllipse(Brushes.SaddleBrown, null, new(86, 46), 5, 7);
            if (kind == 3) dc.DrawEllipse(Brushes.SaddleBrown, null, new(64, 86), 12, 17);
            else if (kind == 6) dc.DrawLine(pen, new(42, 85), new(86, 85));
            else
            {
                var path = new StreamGeometry(); using (var c = path.Open()) { c.BeginFigure(new(35, kind == 2 ? 90 : 78), kind == 1, kind == 1); c.QuadraticBezierTo(new(64, kind == 2 ? 65 : 110), new(93, kind == 2 ? 90 : 78), true, false); }
                dc.DrawGeometry(kind == 1 ? Brushes.SaddleBrown : null, pen, path);
            }
            if (kind == 5) { dc.DrawEllipse(Brushes.LightCoral, null, new(25, 73), 9, 5); dc.DrawEllipse(Brushes.LightCoral, null, new(103, 73), 9, 5); }
            if (kind == 7) { dc.DrawEllipse(Brushes.DodgerBlue, null, new(99, 62), 5, 10); }
        }
        var image = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze(); return image;
    }
}
