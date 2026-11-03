using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Snip.Core;

namespace Snip.Infrastructure;

/// <summary>Tracks only target identity and whether input changed, never keys/text/history.</summary>
public interface IVoiceInputTarget : IDisposable
{
    IntPtr Foreground { get; }
    bool IsCurrent { get; }
    bool HotkeyHeld { get; }
}
public sealed class VoiceInputTarget : IVoiceInputTarget
{
    [StructLayout(LayoutKind.Sequential)] private struct GuiInfo
    { public uint Size, Flags; public IntPtr Active, Focus, Capture, Menu, Move, Caret; public NativeMethods.NativeRect Rect; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint Key, Scan, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    private delegate IntPtr Hook(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, Hook hook, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    private readonly Hook keyboardCallback, mouseCallback;
    private readonly HotkeyGesture gesture;
    private readonly IntPtr focus;
    private readonly uint process;
    private IntPtr keyboard, mouse;
    private volatile bool disposed;
    private Dispatcher? inputDispatcher;
    private volatile bool invalidated;
    public IntPtr Foreground { get; }
    public bool Invalidated { get => invalidated; private set => invalidated = value; }
    public bool IsCurrent
    {
        get
        {
            if (disposed || Invalidated || Foreground == IntPtr.Zero || GetForegroundWindow() != Foreground || !IsWindow(Foreground)) return false;
            GetWindowThreadProcessId(Foreground, out uint actual);
            var info = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() };
            return actual == process && GetGUIThreadInfo(0, ref info) && info.Focus == focus;
        }
    }
    public bool HotkeyHeld => (GetAsyncKeyState((int)gesture.VirtualKey) & 0x8000) != 0 && RequiredModifiersHeld();
    public VoiceInputTarget(HotkeyGesture gesture, Action released, Action cancelled)
    {
        this.gesture = gesture; Foreground = GetForegroundWindow(); GetWindowThreadProcessId(Foreground, out process);
        var info = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() }; if (!GetGUIThreadInfo(0, ref info)) Invalidated = true; focus = info.Focus;
        keyboardCallback = (code, message, data) =>
        {
            if (code >= 0 && !disposed)
            {
                var key = Marshal.PtrToStructure<KeyboardData>(data);
                if (key.Extra != VoiceTextInput.Marker)
                {
                    int normal = Normalize((int)key.Key); bool down = message.ToInt32() is 0x100 or 0x104;
                    if (normal == 0x1B) { if (down) cancelled(); return new(1); }
                    if (!down && (normal == gesture.VirtualKey || IsRequiredModifier(normal))) released();
                    if (down && !IsModifier(normal) && !(normal == gesture.VirtualKey && RequiredModifiersHeld())) Invalidated = true;
                }
            }
            return CallNextHookEx(keyboard, code, message, data);
        };
        mouseCallback = (code, message, data) =>
        {
            if (code >= 0 && message.ToInt32() is 0x201 or 0x204 or 0x207 or 0x20B) Invalidated = true;
            return CallNextHookEx(mouse, code, message, data);
        };
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            inputDispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                keyboard = SetWindowsHookEx(13, keyboardCallback, GetModuleHandle(null), 0);
                mouse = SetWindowsHookEx(14, mouseCallback, GetModuleHandle(null), 0);
                bool success = keyboard != IntPtr.Zero && mouse != IntPtr.Zero; ready.TrySetResult(success);
                if (success && !disposed) Dispatcher.Run();
            }
            finally
            {
                if (keyboard != IntPtr.Zero) UnhookWindowsHookEx(keyboard); if (mouse != IntPtr.Zero) UnhookWindowsHookEx(mouse);
                keyboard = mouse = IntPtr.Zero; ready.TrySetResult(false);
            }
        }) { IsBackground = true, Name = "SelfSnip voice input guard" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!ready.Task.Wait(TimeSpan.FromSeconds(1)) || !ready.Task.GetAwaiter().GetResult())
        { Dispose(); throw new VoiceInputException("无法监测输入位置，语音输入未启动，请重试。"); }
    }
    private static int Normalize(int key) => key switch { 0xA0 or 0xA1 => 0x10, 0xA2 or 0xA3 => 0x11, 0xA4 or 0xA5 => 0x12, _ => key };
    private static bool IsModifier(int key) => key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C;
    private bool IsRequiredModifier(int key) => key switch
    { 0x10 => (gesture.Modifiers & 4) != 0, 0x11 => (gesture.Modifiers & 2) != 0, 0x12 => (gesture.Modifiers & 1) != 0, 0x5B or 0x5C => (gesture.Modifiers & 8) != 0, _ => false };
    private bool RequiredModifiersHeld() => ((gesture.Modifiers & 1) == 0 || GetAsyncKeyState(0x12) < 0) && ((gesture.Modifiers & 2) == 0 || GetAsyncKeyState(0x11) < 0) &&
        ((gesture.Modifiers & 4) == 0 || GetAsyncKeyState(0x10) < 0) && ((gesture.Modifiers & 8) == 0 || GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0);
    public static bool ModifiersReleased => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.All(k => GetAsyncKeyState(k) >= 0);
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        inputDispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
    }
}
public static class VoiceTextInput
{
    public static readonly UIntPtr Marker = new(0x534E564F);
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KeyData Keyboard; [FieldOffset(0)] public MouseData Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyData { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    public static async Task<int> TypeAsync(string text, Func<bool> stillCurrent, CancellationToken token)
    {
        text = VoiceText.ForTyping(text);
        for (int i = 0; !VoiceInputTarget.ModifiersReleased && i < 75; i++) await Task.Delay(20, token);
        if (!VoiceInputTarget.ModifiersReleased || !stillCurrent()) return 0;
        int typed = 0;
        while (typed < text.Length)
        {
            if (token.IsCancellationRequested || !stillCurrent()) return typed;
            int count = Math.Min(64, text.Length - typed); if (char.IsHighSurrogate(text[typed + count - 1])) count--;
            var inputs = new List<Input>();
            for (int i = 0; i < count; i++)
            {
                ushort character = text[typed + i];
                inputs.Add(new() { Type = 1, Data = new() { Keyboard = new() { Scan = character, Flags = 4, Extra = Marker } } });
                inputs.Add(new() { Type = 1, Data = new() { Keyboard = new() { Scan = character, Flags = 6, Extra = Marker } } });
            }
            uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
            typed += (int)sent / 2; if (sent != inputs.Count) return typed;
            await Task.Delay(1);
        }
        return typed;
    }
}
