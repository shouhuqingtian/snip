using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Snip.App.Services;
using Snip.Infrastructure;

namespace Snip.App.Views;

public sealed class VoiceInputStatusWindow : Window
{
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetLong(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock timerText = new() { FontSize = 11, Foreground = Brushes.LightGray, Margin = new Thickness(0, 5, 0, 0) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly VoiceInputController controller;
    private readonly Button review;
    private DateTimeOffset updated;
    private HwndSource? source;
    public VoiceInputStatusWindow(VoiceInputController controller, Action showResult)
    {
        this.controller = controller; Style = null; Title = "自用截图 · 语音输入";
        Width = 430; Height = 112; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false; Focusable = false; Topmost = true;
        AllowsTransparency = true; Background = Brushes.Transparent;
        var content = new DockPanel { Margin = new Thickness(14) };
        var cancel = new Button { Content = "×", Width = 28, Height = 28, MinHeight = 28, Padding = new Thickness(0), Foreground = Brushes.White, FontSize = 19, Margin = new Thickness(8, 0, 0, 0), ToolTip = "取消 / 关闭提示", VerticalAlignment = VerticalAlignment.Top };
        Ui.Style(cancel, "QuietButton");
        cancel.Click += (_, _) => { controller.Cancel(true); Close(); }; DockPanel.SetDock(cancel, Dock.Right); content.Children.Add(cancel);
        var text = new StackPanel(); text.Children.Add(message); text.Children.Add(timerText);
        review = new Button { Content = "查看 / 复制识别结果", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 5, 0, 0), Visibility = Visibility.Collapsed };
        review.Click += (_, _) => { showResult(); Close(); }; text.Children.Add(review); content.Children.Add(text);
        Content = new Border { Background = new SolidColorBrush(Color.FromArgb(245, 35, 54, 57)), CornerRadius = new CornerRadius(12), Child = content };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle; SetLong(handle, -20, new(GetLong(handle, -20).ToInt64() | 0x08000000L | 0x80));
            source = HwndSource.FromHwnd(handle); source?.AddHook(Hook);
        };
        Loaded += (_, _) =>
        {
            var desktop = new DesktopCaptureService(); var cursor = desktop.CursorPosition;
            var display = desktop.GetDisplays().FirstOrDefault(d => d.Bounds.Contains(cursor)) ?? desktop.GetDisplays()[0];
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowPos(handle, new(-1), (int)display.Bounds.X + 12, (int)display.Bounds.Y + 12, 0, 0, 0x11);
            double scale = Math.Max(96, DesktopWindows.GetDpiForWindow(handle)) / 96.0;
            int width = Math.Min((int)(Width * scale), (int)display.Bounds.Width - 24), height = Math.Min((int)(Height * scale), (int)display.Bounds.Height - 24);
            SetWindowPos(handle, new(-1), (int)(display.Bounds.X + (display.Bounds.Width - width) / 2), (int)display.Bounds.Bottom - height - 64, width, height, 0x10);
            UpdateStatus(); timer.Start();
        };
        timer.Tick += (_, _) =>
        {
            if (controller.State == VoiceInputState.Recording && controller.RecordingStartedUtc is { } started) timerText.Text = $"已录音 {(DateTimeOffset.UtcNow - started).TotalSeconds:0} 秒 · 松开结束 · Esc 取消";
            else timerText.Text = controller.IsActive ? "按住录音，松开识别；文字不会自动发送。" : "";
            if (!controller.IsActive && !controller.ReviewNeeded && DateTimeOffset.UtcNow - updated > TimeSpan.FromSeconds(3)) Close();
        };
        Closed += (_, _) => { timer.Stop(); source?.RemoveHook(Hook); };
    }
    public void UpdateStatus()
    {
        message.Text = controller.Status; updated = DateTimeOffset.UtcNow;
        review.Visibility = controller.ReviewNeeded && controller.LastText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private static IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    { if (message == 0x21) { handled = true; return new(3); } return IntPtr.Zero; }
}
