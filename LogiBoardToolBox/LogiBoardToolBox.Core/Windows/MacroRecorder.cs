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
    private readonly Dictionary<uint, string> heldKeys = [];
    private readonly MouseRecordingFilter mouseFilter = new();
    private readonly Stopwatch timer = new();
    private IntPtr keyboardHook, mouseHook;
    private Func<int, int, bool>? isStopRecordingTarget;
    private long lastEvent;
    private bool hasEvent;
    private bool stopping;
    public event EventHandler<string>? StepRecorded;
    public bool IsRecording => keyboardHook != IntPtr.Zero;
    public MacroRecorder() { keyboardCallback = Keyboard; mouseCallback = Mouse; }
    public void Start(IntPtr ownWindow) => Start(ownWindow, null);
    /// <param name="ownWindow">Retained for compatibility; the application window is not excluded.</param>
    /// <param name="isStopRecordingTarget">Hit-tests the actual stop control using physical screen coordinates.</param>
    public void Start(IntPtr ownWindow, Func<int, int, bool>? isStopRecordingTarget)
    {
        if (IsRecording || stopping) return;
        this.isStopRecordingTarget = isStopRecordingTarget;
        heldKeys.Clear(); mouseFilter.Reset(); lastEvent = 0; hasEvent = false;
        keyboardHook = SetWindowsHookEx(13, keyboardCallback, GetModuleHandle(null), 0);
        mouseHook = SetWindowsHookEx(14, mouseCallback, GetModuleHandle(null), 0);
        if (keyboardHook == IntPtr.Zero || mouseHook == IntPtr.Zero)
        { var error = Marshal.GetLastWin32Error(); Dispose(); throw new System.ComponentModel.Win32Exception(error, "无法开始宏录制。"); }
        timer.Restart();
    }
    private IntPtr Keyboard(int code, IntPtr message, IntPtr data)
    {
        try
        {
            if (code >= 0)
            {
                var input = Marshal.PtrToStructure<KeyboardData>(data);
                if ((input.Flags & 0x10) == 0 && KeyName(input.VirtualKey, input.ScanCode, input.Flags) is { } key)
                {
                    var down = message.ToInt64() is 0x100 or 0x104;
                    var up = message.ToInt64() is 0x101 or 0x105;
                    if (down && heldKeys.TryAdd(input.VirtualKey, key)) Record($"key {key} down");
                    else if (up && heldKeys.Remove(input.VirtualKey, out var pressedKey)) Record($"key {pressedKey} up");
                }
            }
        }
        catch { /* Native hook callbacks must always return control to Windows. */ }
        return CallNextHookEx(keyboardHook, code, message, data);
    }
    private IntPtr Mouse(int code, IntPtr message, IntPtr data)
    {
        try
        {
            if (code >= 0)
            {
                var input = Marshal.PtrToStructure<MouseHookData>(data);
                if ((input.Flags & 3) == 0)
                {
                    var msg = message.ToInt64();
                    var button = msg switch { 0x201 or 0x202 => 1, 0x204 or 0x205 => 2, 0x207 or 0x208 => 3, 0x20B or 0x20C => 3 + (int)(input.MouseData >> 16), _ => 0 };
                    if (button != 0)
                    {
                        var down = msg is 0x201 or 0x204 or 0x207 or 0x20B;
                        var stopTarget = down && IsStopTarget(input.Point);
                        if (mouseFilter.ShouldRecordButton(button, down, injected: false, stopTarget))
                            Record($"mouse {button} {(down ? "down" : "up")}");
                    }
                    else if (msg is 0x20A or 0x20E)
                        Record($"{(msg == 0x20A ? "wheel" : "pan")} {Math.Clamp((short)(input.MouseData >> 16) / 120, -127, 127)}");
                }
            }
        }
        catch { /* Recording never blocks or consumes physical mouse input. */ }
        return CallNextHookEx(mouseHook, code, message, data);
    }
    private bool IsStopTarget(Point point)
    {
        try { return isStopRecordingTarget?.Invoke(point.X, point.Y) == true; }
        catch { return false; }
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
        if (stopping) return;
        stopping = true;
        try
        {
            if (keyboardHook != IntPtr.Zero) { UnhookWindowsHookEx(keyboardHook); keyboardHook = IntPtr.Zero; }
            if (mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
            var keys = heldKeys.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
            heldKeys.Clear();
            var buttons = mouseFilter.ReleaseRecordedButtons();
            isStopRecordingTarget = null;
            // Finalize the recorded gesture, not physical input. A step handler may itself request Stop.
            foreach (var key in keys) Record($"key {key} up");
            foreach (var button in buttons) Record($"mouse {button} up");
        }
        finally { timer.Stop(); stopping = false; }
    }
    public void Dispose() => Stop();
    private delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint VirtualKey, ScanCode, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseHookData { public Point Point; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int kind, HookCallback callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
}
