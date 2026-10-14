using System.Windows;
using System.Windows.Input;
using Snip.App.ViewModels;
using Snip.Core;

namespace Snip.App.Views;

public partial class HotkeySettingsWindow : Window
{
    private readonly HotkeySettingsViewModel vm;
    public HotkeySettingsWindow(HotkeySettingsViewModel vm)
    {
        this.vm = vm; Ui.ApplyWindow(this); InitializeComponent(); DataContext = vm;
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } };
        Loaded += (_, _) => GestureBox.Focus();
    }
    private void CaptureKey(object sender, KeyEventArgs args)
    {
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        if (key == Key.Escape) return;
        args.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        uint modifiers = 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= 1;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= 2;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= 4;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= 8;
        vm.SetGesture(new HotkeyGesture(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key)));
    }
    private void CloseClick(object sender, RoutedEventArgs args) => Close();
}
