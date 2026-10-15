using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Snip.App.Views;

/// <summary>Consistent 24 px vector icons, independent of installed symbol fonts.</summary>
public sealed class UiIcon : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = Control.ForegroundProperty.AddOwner(typeof(UiIcon), new FrameworkPropertyMetadata(Brushes.DarkSlateGray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(UiIcon), new FrameworkPropertyMetadata("capture", FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    private static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>
    {
        ["capture"] = "M 4 9 L 4 4 L 9 4 M 15 4 L 20 4 L 20 9 M 20 15 L 20 20 L 15 20 M 9 20 L 4 20 L 4 15 M 8 8 L 16 8 L 16 16 L 8 16 Z",
        ["select"] = "M 5 3 L 5 19 L 9 15 L 12 21 L 15 19 L 12 13 L 18 13 Z",
        ["rectangle"] = "M 4 5 L 20 5 L 20 19 L 4 19 Z",
        ["ellipse"] = "M 20 12 A 8 8 0 1 1 4 12 A 8 8 0 1 1 20 12 Z",
        ["arrow"] = "M 5 19 L 19 5 M 9 5 L 19 5 L 19 15",
        ["pen"] = "M 4 20 L 5 14 L 16 3 L 21 8 L 10 19 Z M 14 5 L 19 10",
        ["mosaic"] = "M 4 4 L 20 4 L 20 20 L 4 20 Z M 9 4 L 9 20 M 15 4 L 15 20 M 4 9 L 20 9 M 4 15 L 20 15",
        ["text"] = "M 4 5 L 20 5 M 12 5 L 12 20 M 8 20 L 16 20",
        ["sticker"] = "M 21 12 A 9 9 0 1 1 3 12 A 9 9 0 1 1 21 12 Z M 8 9 L 8 10 M 16 9 L 16 10 M 7 14 Q 12 21 17 14",
        ["ocr"] = "M 3 8 L 3 3 L 8 3 M 16 3 L 21 3 L 21 8 M 21 16 L 21 21 L 16 21 M 8 21 L 3 21 L 3 16 M 7 8 L 17 8 M 7 12 L 17 12 M 7 16 L 13 16",
        ["translate"] = "M 3 5 L 14 5 M 9 3 L 9 5 M 5 8 Q 8 13 13 15 M 12 5 Q 11 12 3 16 M 13 21 L 17 11 L 21 21 M 15 17 L 19 17",
        ["scroll"] = "M 5 9 L 5 3 L 19 3 L 19 9 M 19 15 L 19 21 L 5 21 L 5 15 M 12 6 L 12 18 M 9 9 L 12 6 L 15 9 M 9 15 L 12 18 L 15 15",
        ["undo"] = "M 8 4 L 3 9 L 8 14 M 3 9 L 14 9 Q 21 9 21 15 Q 21 20 15 20",
        ["redo"] = "M 16 4 L 21 9 L 16 14 M 21 9 L 10 9 Q 3 9 3 15 Q 3 20 9 20",
        ["save"] = "M 5 13 L 5 21 L 19 21 L 19 13 M 12 3 L 12 16 M 7 11 L 12 16 L 17 11",
        ["pin"] = "M 9 3 L 15 3 L 14 9 L 18 14 L 6 14 L 10 9 Z M 12 14 L 12 22",
        ["share"] = "M 13 4 L 21 11 L 13 18 L 13 13 Q 6 13 3 20 Q 2 8 13 8 Z",
        ["editor"] = "M 3 4 L 21 4 L 21 20 L 3 20 Z M 3 8 L 21 8 M 7 6 L 8 6",
        ["adjust"] = "M 4 8 L 4 4 L 8 4 M 16 4 L 20 4 L 20 8 M 20 16 L 20 20 L 16 20 M 8 20 L 4 20 L 4 16 M 8 12 L 16 12 M 12 8 L 12 16",
        ["close"] = "M 6 6 L 18 18 M 18 6 L 6 18",
        ["check"] = "M 4 12 L 9 17 L 20 6",
        ["copy"] = "M 8 8 L 20 8 L 20 21 L 8 21 Z M 15 8 L 15 3 L 3 3 L 3 16 L 8 16",
        ["speaker"] = "M 4 9 L 8 9 L 13 5 L 13 19 L 8 15 L 4 15 Z M 16 8 Q 20 12 16 16 M 19 5 Q 25 12 19 19",
        ["expand"] = "M 9 4 L 4 4 L 4 9 M 15 4 L 20 4 L 20 9 M 20 15 L 20 20 L 15 20 M 9 20 L 4 20 L 4 15"
    };
    private static readonly IReadOnlyDictionary<string, Geometry> Geometries = Paths.ToDictionary(pair => pair.Key, pair =>
    {
        var geometry = Geometry.Parse(pair.Value); geometry.Freeze(); return geometry;
    });
    public UiIcon() : this("capture") { }
    public UiIcon(string kind)
    {
        Kind = kind; Width = Height = 24; IsHitTestVisible = false;
        SetBinding(ForegroundProperty, new Binding(nameof(Control.Foreground)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1) });
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!Geometries.TryGetValue(Kind, out var geometry)) return;
        double scale = Math.Min(ActualWidth, ActualHeight) / 24;
        dc.PushTransform(new TranslateTransform((ActualWidth - 24 * scale) / 2, (ActualHeight - 24 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(null, new Pen(Foreground, 1.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geometry);
        dc.Pop(); dc.Pop();
    }
}
