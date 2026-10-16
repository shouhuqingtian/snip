using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.Windows.Checks;

internal static class HotkeyChecks
{
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Wheel, Flags, Time; public UIntPtr Extra; }
    public static int Run()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        string directory = Path.Combine(Path.GetTempPath(), "Snip.Hotkey.Check-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            using var service = new HotkeyService(); using var occupant = new HotkeyService();
            var first = new HotkeyGesture(3, 0x78); // Ctrl+Alt+F9
            var taken = new HotkeyGesture(3, 0x79); // Ctrl+Alt+F10
            var replacement = new HotkeyGesture(3, 0x7A); // Ctrl+Alt+F11
            Require(service.Register(first) && occupant.Register(taken), "Could not register public hotkey fixtures.");
            int triggers = 0; service.Triggered += (_, _) => triggers++;
            Require(!service.Register(taken) && service.Current == first, "Conflict lost the old registration.");
            Press(0x11, 0x12, 0x78); TextWindowChecks.PumpUntil(() => triggers == 1, TimeSpan.FromSeconds(3));
            Console.WriteLine("PASS real RegisterHotKey conflict preserves previous key; real keyboard input still triggers it.");

            Require(service.Register(replacement), "Could not change native shortcut.");
            Press(0x11, 0x12, 0x78); PumpBriefly(); Require(triggers == 1, "Old key still triggered after replacement.");
            Press(0x11, 0x12, 0x7A); TextWindowChecks.PumpUntil(() => triggers == 2, TimeSpan.FromSeconds(3));
            Console.WriteLine("PASS shortcut replacement releases old key and activates new key without restart.");

            var path = Path.Combine(directory, "hotkey.json"); var store = new HotkeySettingsStore(path);
            var failed = new HotkeySettingsViewModel(service, new FailingStore(), replacement);
            failed.SetGesture(first); failed.SaveCommand.Execute(null);
            Require(service.Current == replacement && failed.Status.Contains("恢复"), "Failed save did not roll back registration.");
            var vm = new HotkeySettingsViewModel(service, store, replacement);
            vm.ResetCommand.Execute(null);
            Require(vm.GestureText == "Ctrl+Alt+A" && service.Current == replacement, "Reset applied an unsaved draft.");
            vm.SaveCommand.Execute(null);
            Require(service.Current == HotkeyGesture.Default && new HotkeySettingsStore(path).Read() == HotkeyGesture.Default, "Default hotkey did not persist.");
            Console.WriteLine("PASS write failure restores working key; reset is a draft until saved; settings reload after save.");

            var window = new HotkeySettingsWindow(vm); window.Show(); window.Activate(); window.UpdateLayout();
            ((TextBox)window.FindName("GestureBox")).Focus(); PumpBriefly();
            Press(0x11, 0x10, 0x41); TextWindowChecks.PumpUntil(() => vm.GestureText == "Ctrl+Shift+A", TimeSpan.FromSeconds(3));
            vm.SaveCommand.Execute(null);
            Require(service.Current == new HotkeyGesture(6, 0x41) && new HotkeySettingsStore(path).Read() == service.Current, "Actual settings window could not capture and save a key combination.");
            Press(0x1B); TextWindowChecks.PumpUntil(() => !window.IsVisible, TimeSpan.FromSeconds(3));
            Console.WriteLine("PASS real WPF settings records Ctrl+Shift+A keyboard input, saves it and closes with Esc.");

            File.WriteAllText(path, "{broken");
            bool invalid = false; try { store.Read(); } catch (InvalidOperationException) { invalid = true; }
            Require(invalid, "Damaged shortcut configuration was silently accepted.");
            Console.WriteLine("PASS invalid saved configuration is reported for default fallback.");
            SelectionChecks.Verify(service);
            SelfCaptureChecks.Verify(service);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            foreach (Window window in app.Windows.Cast<Window>().ToArray()) window.Close();
            SynchronizationContext.SetSynchronizationContext(previous); Directory.Delete(directory, true);
        }
    }
    internal static void Press(params ushort[] keys)
    {
        var inputs = keys.Select(key => new Input { Type = 1, Data = new InputUnion { Keyboard = new() { Key = key } } })
            .Concat(keys.Reverse().Select(key => new Input { Type = 1, Data = new InputUnion { Keyboard = new() { Key = key, Flags = 2 } } })).ToArray();
        Require(SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length, "Native keyboard input was rejected.");
    }
    private static void PumpBriefly()
    {
        var delay = Task.Delay(250); TextWindowChecks.PumpUntil(() => delay.IsCompleted, TimeSpan.FromSeconds(2));
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class FailingStore : IHotkeySettingsStore
    {
        public HotkeyGesture Read() => HotkeyGesture.Default;
        public void Save(HotkeyGesture value) => throw new IOException("Public simulated save failure.");
    }
}
