using System.Windows;
using System.Windows.Input;
using Snip.App.ViewModels;
using Snip.Core;

namespace Snip.App.Views;

public partial class VoiceInputSettingsWindow : Window
{
    private readonly VoiceInputSettingsViewModel vm;
    public VoiceInputSettingsWindow(VoiceInputSettingsViewModel vm)
    {
        this.vm = vm; Ui.ApplyWindow(this); InitializeComponent(); DataContext = vm;
        vm.SecretsCleared += ClearKey; Closed += (_, _) => { vm.SecretsCleared -= ClearKey; VoiceKeyBox.Clear(); vm.Dispose(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }
    private void ClearKey(object? sender, EventArgs args) => VoiceKeyBox.Clear();
    public void CaptureRegisteredGesture(HotkeyGesture gesture) => vm.SetGesture(gesture);
    private void KeyChanged(object sender, RoutedEventArgs args) => vm.ApiKey = VoiceKeyBox.Password;
    private void CaptureKey(object sender, KeyEventArgs args)
    {
        var key = args.Key == Key.System ? args.SystemKey : args.Key; if (key == Key.Escape) return;
        args.Handled = true; if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        uint modifiers = 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= 1;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= 2;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= 4;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= 8;
        vm.SetGesture(new(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key)));
    }
    private void CloseClick(object sender, RoutedEventArgs args) => Close();
}
