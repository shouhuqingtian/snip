using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Snip.App.Services;
using Snip.Infrastructure;
using Snip.Core;

namespace Snip.App.Views;

public sealed class VoiceInputStatusWindow : Window
{
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetLong(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(7, 151, 91));
    private static readonly Geometry Microphone = Geometry.Parse("M8,1 C6.3,1 5,2.3 5,4 L5,8 C5,9.7 6.3,11 8,11 C9.7,11 11,9.7 11,8 L11,4 C11,2.3 9.7,1 8,1 M2,7 L2,8 C2,11.3 4.7,14 8,14 C11.3,14 14,11.3 14,8 L14,7 M8,14 L8,17 M4.5,17 L11.5,17");
    private static readonly Geometry Spinner = Geometry.Parse("M8,1 A7,7 0 1 1 1,8");
    private static readonly Geometry Check = Geometry.Parse("M2,8 L6,12 L14,3");
    private static readonly Geometry Warning = Geometry.Parse("M8,2 L8,10 M8,14 L8,14.5");
    private readonly TextBlock title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock detail = new() { FontSize = 11, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Path icon = new() { Width = 18, Height = 21, Stretch = Stretch.Uniform, StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, RenderTransformOrigin = new Point(.5, .5) };
    private readonly RotateTransform rotation = new();
    private readonly Border surface;
    private readonly Button review, close;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly VoiceInputController controller;
    private DateTimeOffset updated, phaseStarted;
    private VoiceInputState previousState;
    private PixelRect displayBounds;
    private HwndSource? source;
    private int frame;
    private bool light;

    public VoiceInputStatusWindow(VoiceInputController controller, Action showResult)
    {
        this.controller = controller; Style = null; Title = "自用截图 · 语音输入";
        Width = 332; Height = 78; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Focusable = false; Topmost = true;
        AllowsTransparency = true; Background = Brushes.Transparent;
        var row = new DockPanel { LastChildFill = true };
        close = FlatButton("×", "取消 / 关闭提示"); close.Width = 26; close.Height = 26; close.FontSize = 19; close.Margin = new Thickness(10, 0, 0, 0);
        close.Click += (_, _) => { controller.Cancel(true); Close(); };
        DockPanel.SetDock(close, Dock.Right); row.Children.Add(close);
        icon.Margin = new Thickness(0, 0, 13, 0); icon.VerticalAlignment = VerticalAlignment.Center;
        icon.RenderTransform = rotation; DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; text.Children.Add(title); text.Children.Add(detail);
        review = FlatButton("查看并复制结果", "打开识别结果"); review.FontSize = 12; review.Margin = new Thickness(0, 6, 0, 0); review.HorizontalAlignment = HorizontalAlignment.Left; review.Padding = new Thickness(0, 3, 0, 3); review.Visibility = Visibility.Collapsed;
        review.Click += (_, _) => { showResult(); Close(); }; text.Children.Add(review); row.Children.Add(text);
        surface = new Border { CornerRadius = new CornerRadius(29), Padding = new Thickness(19, 10, 12, 10), Margin = new Thickness(9), Child = row,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = .17, Color = Colors.Black } };
        Content = surface;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle; SetLong(handle, -20, new(GetLong(handle, -20).ToInt64() | 0x08000000L | 0x80));
            source = HwndSource.FromHwnd(handle); source?.AddHook(Hook);
        };
        Loaded += (_, _) =>
        {
            var desktop = new DesktopCaptureService(); var cursor = desktop.CursorPosition;
            displayBounds = (desktop.GetDisplays().FirstOrDefault(d => d.Bounds.Contains(cursor)) ?? desktop.GetDisplays()[0]).Bounds;
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowPos(handle, new(-1), (int)displayBounds.X + 12, (int)displayBounds.Y + 12, 0, 0, 0x11);
            UpdateStatus(); timer.Start();
        };
        timer.Tick += (_, _) =>
        {
            frame++;
            if (controller.State is VoiceInputState.Starting or VoiceInputState.Recognizing) rotation.Angle = (frame * 24) % 360;
            else { rotation.Angle = 0; icon.Opacity = controller.State == VoiceInputState.Recording ? .75 + .25 * Math.Sin(frame * .3) : 1; }
            if (controller.State == VoiceInputState.Recording && controller.RecordingStartedUtc is { } started)
                detail.Text = $"{(DateTimeOffset.UtcNow - started).TotalSeconds:0} 秒 · {controller.RecordingEndHint}";
            else if (controller.State == VoiceInputState.Recognizing) detail.Text = $"已等待 {(DateTimeOffset.UtcNow - phaseStarted).TotalSeconds:0} 秒 · Esc 取消";
            if (!controller.IsActive && !controller.ReviewNeeded && DateTimeOffset.UtcNow - updated > TimeSpan.FromSeconds(light ? 8 : 3)) Close();
        };
        Closed += (_, _) => { timer.Stop(); source?.RemoveHook(Hook); };
    }
    private static Button FlatButton(string text, string tooltip)
    {
        var button = new Button { Style = null, Content = text, ToolTip = tooltip, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.Hand, Focusable = false };
        var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(13)); border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(presenter);
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        button.MouseEnter += (_, _) => button.Opacity = .65; button.MouseLeave += (_, _) => button.Opacity = 1;
        return button;
    }
    public void UpdateStatus()
    {
        if (controller.State != previousState) { previousState = controller.State; phaseStarted = DateTimeOffset.UtcNow; }
        updated = DateTimeOffset.UtcNow;
        bool failure = controller.LastFailure.Length > 0 && !controller.IsActive;
        bool result = controller.ReviewNeeded && controller.LastText.Length > 0 && !controller.IsActive;
        light = failure || result;
        Width = failure ? 440 : result ? 370 : controller.IsActive ? 332 : 300;
        Height = failure ? 144 : result ? 118 : 78;
        surface.Background = light ? Brushes.White : Green; surface.CornerRadius = new CornerRadius(light ? 20 : 29);
        title.Foreground = light ? new SolidColorBrush(Color.FromRgb(36, 44, 40)) : Brushes.White;
        detail.Foreground = light ? new SolidColorBrush(Color.FromRgb(91, 102, 95)) : new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
        close.Foreground = light ? Brushes.Gray : Brushes.White; review.Foreground = Green;
        icon.Stroke = failure ? new SolidColorBrush(Color.FromRgb(190, 113, 26)) : light ? Green : Brushes.White;
        icon.Data = controller.State is VoiceInputState.Starting or VoiceInputState.Recognizing ? Spinner : controller.State == VoiceInputState.Recording ? Microphone : failure ? Warning : Check;
        icon.Opacity = 1; rotation.Angle = 0;
        title.Text = controller.State switch
        {
            VoiceInputState.Starting => "准备录音…",
            VoiceInputState.Recording => "正在聆听",
            VoiceInputState.Recognizing => "正在识别…",
            VoiceInputState.Inserting => "正在输入…",
            _ => failure ? "语音输入未完成" : result ? "文字已识别" : controller.Status.Contains("已取消") ? "已取消" : controller.Status.Contains("已保存") ? "设置已保存" : controller.Status.StartsWith("识别完成") ? "已输入" : controller.Status.Contains("已启用") ? "语音输入已启用" : "语音输入"
        };
        detail.TextWrapping = failure ? TextWrapping.Wrap : TextWrapping.NoWrap;
        detail.MaxHeight = failure ? 84 : double.PositiveInfinity;
        detail.Text = failure ? controller.Status : result ? "未自动输入，结果已保留" : controller.State == VoiceInputState.Starting ? "Esc 取消" : controller.State == VoiceInputState.Recording ? controller.RecordingEndHint : controller.State == VoiceInputState.Inserting ? "不会自动发送消息" : controller.State == VoiceInputState.Recognizing ? "已等待 0 秒 · Esc 取消" : controller.Status.Contains("识别完成") ? controller.LastText : controller.Status.Contains("已取消") ? "录音与识别已停止" : "";
        detail.Visibility = detail.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        review.Visibility = result ? Visibility.Visible : Visibility.Collapsed;
        surface.ToolTip = controller.Status;
        if (IsLoaded) Position();
    }
    private void Position()
    {
        var handle = new WindowInteropHelper(this).Handle;
        double scale = Math.Max(96, DesktopWindows.GetDpiForWindow(handle)) / 96.0;
        int width = Math.Min((int)Math.Ceiling(Width * scale), (int)displayBounds.Width - 24), height = Math.Min((int)Math.Ceiling(Height * scale), (int)displayBounds.Height - 24);
        SetWindowPos(handle, new(-1), (int)(displayBounds.X + (displayBounds.Width - width) / 2), (int)displayBounds.Bottom - height - (int)(44 * scale), width, height, 0x10);
    }
    private static IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    { if (message == 0x21) { handled = true; return new(3); } return IntPtr.Zero; }
}
