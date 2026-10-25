using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LogiBoardToolBox.Core.Macros;

namespace LogiBoardToolBox.Core.Windows;

/// <summary>Records only while explicitly started. Hook callbacks never suppress user input.</summary>
[SupportedOSPlatform("windows")]
public sealed class MacroRecorder : IDisposable
{
    private readonly HookCallback keyboardCallback;
    private readonly HookCallback mouseCallback;
    private readonly HashSet<uint> heldKeys = [];
    private readonly HashSet<int> heldMouse = [];
    private readonly Stopwatch timer = new();
    private IntPtr keyboardHook, mouseHook;
    private IntPtr applicationWindow;
    private long lastEvent;
    private bool hasEvent;
    public event EventHandler<string>? StepRecorded;
    public bool IsRecording => keyboardHook != IntPtr.Zero;
    public MacroRecorder() { keyboardCallback = Keyboard; mouseCallback = Mouse; }
    public void Start(IntPtr ownWindow)
    {
        if (IsRecording) return;
        applicationWindow = ownWindow; heldKeys.Clear(); heldMouse.Clear(); lastEvent = 0; hasEvent = false;
        keyboardHook = SetWindowsHookEx(13, keyboardCallback, GetModuleHandle(null), 0);
        mouseHook = SetWindowsHookEx(14, mouseCallback, GetModuleHandle(null), 0);
        if (keyboardHook == IntPtr.Zero || mouseHook == IntPtr.Zero)
        { var error = Marshal.GetLastWin32Error(); Dispose(); throw new System.ComponentModel.Win32Exception(error, "无法开始宏录制。"); }
        timer.Restart();
    }
    private IntPtr Keyboard(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var input = Marshal.PtrToStructure<KeyboardData>(data);
            if ((input.Flags & 0x10) == 0 && KeyName(input.VirtualKey, input.ScanCode, input.Flags) is { } key)
            {
                var down = message.ToInt64() is 0x100 or 0x104;
                var up = message.ToInt64() is 0x101 or 0x105;
                if (down && heldKeys.Add(input.VirtualKey)) Record($"key {key} down");
                else if (up && heldKeys.Remove(input.VirtualKey)) Record($"key {key} up");
            }
        }
        return CallNextHookEx(keyboardHook, code, message, data);
    }
    private IntPtr Mouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var input = Marshal.PtrToStructure<MouseHookData>(data);
            // Clicking the editor/Stop button is not a recorded action.
            if ((input.Flags & 1) == 0 && GetAncestor(WindowFromPoint(input.Point), 2) != applicationWindow)
            {
                var msg = message.ToInt64();
                var button = msg switch { 0x201 or 0x202 => 1, 0x204 or 0x205 => 2, 0x207 or 0x208 => 3, 0x20B or 0x20C => 3 + (int)(input.MouseData >> 16), _ => 0 };
                if (button != 0)
                {
                    var down = msg is 0x201 or 0x204 or 0x207 or 0x20B;
                    if (down && heldMouse.Add(button)) Record($"mouse {button} down");
                    else if (!down && heldMouse.Remove(button)) Record($"mouse {button} up");
                }
                else if (msg is 0x20A or 0x20E)
                    Record($"{(msg == 0x20A ? "wheel" : "pan")} {Math.Clamp((short)(input.MouseData >> 16) / 120, -127, 127)}");
            }
        }
        return CallNextHookEx(mouseHook, code, message, data);
    }
    private void Record(string step)
    {
        try
        {
            var now = timer.ElapsedMilliseconds;
            if (hasEvent)
            {
                var delay = now - lastEvent;
                while (delay > 0) { var part = Math.Min(delay, 65535); StepRecorded?.Invoke(this, $"delay {part}"); delay -= part; }
            }
            StepRecorded?.Invoke(this, step); lastEvent = now; hasEvent = true;
        }
        catch { /* Never propagate managed exceptions through a native hook. */ }
    }
    private static string? KeyName(uint vk, uint scan, uint flags)
    {
        if (vk is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return $"F{vk - 0x6F}";
        if (vk is >= 0x60 and <= 0x69) return $"Numpad{vk - 0x60}";
        return vk switch
        {
            0x10 => scan == 0x36 ? "RightShift" : "Shift", 0x11 => (flags & 1) != 0 ? "RightCtrl" : "Ctrl",
            0x12 => (flags & 1) != 0 ? "RightAlt" : "Alt", 0xA0 => "Shift", 0xA1 => "RightShift", 0xA2 => "Ctrl", 0xA3 => "RightCtrl", 0xA4 => "Alt", 0xA5 => "RightAlt",
            0x5B => "Win", 0x5C => "RightWin", 8 => "Backspace", 9 => "Tab", 13 => "Enter", 27 => "Escape", 32 => "Space",
            33 => "PageUp", 34 => "PageDown", 35 => "End", 36 => "Home", 37 => "Left", 38 => "Up", 39 => "Right", 40 => "Down", 45 => "Insert", 46 => "Delete",
            0xBA => "Semicolon", 0xBB => "Equal", 0xBC => "Comma", 0xBD => "Minus", 0xBE => "Period", 0xBF => "Slash", 0xC0 => "Grave", 0xDB => "LeftBracket", 0xDC => "Backslash", 0xDD => "RightBracket", 0xDE => "Quote",
            _ => null
        };
    }
    public void Stop()
    {
        if (keyboardHook != IntPtr.Zero) { UnhookWindowsHookEx(keyboardHook); keyboardHook = IntPtr.Zero; }
        if (mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
        timer.Stop();
    }
    public void Dispose() => Stop();
    private delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint VirtualKey, ScanCode, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseHookData { public Point Point; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int kind, HookCallback callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
}
