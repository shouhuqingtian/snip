using System.Windows;
using System.Windows.Input;
using Snip.App.ViewModels;
using Snip.Core;

namespace Snip.App.Views;

public partial class VoiceInputSettingsWindow : Window
{
    private readonly VoiceInputSettingsViewModel vm;
    public bool IsCapturingGesture { get; private set; }
    public VoiceInputSettingsWindow(VoiceInputSettingsViewModel vm)
    {
        this.vm = vm; Ui.ApplyWindow(this); InitializeComponent(); DataContext = vm;
        vm.SecretsCleared += ClearKey; Closed += (_, _) => { vm.SecretsCleared -= ClearKey; VoiceKeyBox.Clear(); vm.Dispose(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; if (IsCapturingGesture) EndGestureCapture(); else Close(); } };
        Deactivated += (_, _) => EndGestureCapture();
        VoiceGestureBox.LostKeyboardFocus += (_, _) => EndGestureCapture();
    }
    private void ClearKey(object? sender, EventArgs args) => VoiceKeyBox.Clear();
    public bool TryCaptureRegisteredGesture(HotkeyGesture gesture)
    {
        if (!IsActive || !IsCapturingGesture || !VoiceGestureBox.IsKeyboardFocused || !vm.CanEdit) return false;
        if (vm.SetGesture(gesture)) EndGestureCapture(); return true;
    }
    private void BeginGestureCapture(object sender, MouseButtonEventArgs args) => BeginGestureCapture();
    private void ChangeGestureClick(object sender, RoutedEventArgs args) => BeginGestureCapture();
    private void BeginGestureCapture()
    {
        if (!vm.CanEdit) return;
        IsCapturingGesture = true; VoiceGestureBox.Focus();
        GestureCaptureHint.Text = "正在录入：请按下新组合，Esc 或点击其他位置取消。保存后生效。";
    }
    private void EndGestureCapture()
    {
        IsCapturingGesture = false;
        GestureCaptureHint.Text = "点击修改后才录入快捷键；保存后生效。";
    }
    private void KeyChanged(object sender, RoutedEventArgs args) => vm.ApiKey = VoiceKeyBox.Password;
    private void CaptureKey(object sender, KeyEventArgs args)
    {
        if (!IsCapturingGesture || !vm.CanEdit) return;
        var key = args.Key == Key.System ? args.SystemKey : args.Key == Key.ImeProcessed ? args.ImeProcessedKey : args.Key == Key.DeadCharProcessed ? args.DeadCharProcessedKey : args.Key; if (key == Key.Escape) return;
        if (key == Key.Tab) { EndGestureCapture(); return; }
        args.Handled = true; if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        uint modifiers = 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= 1;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= 2;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= 4;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= 8;
        if (vm.SetGesture(new(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key)))) EndGestureCapture();
    }
    private void CloseClick(object sender, RoutedEventArgs args) => Close();
}
