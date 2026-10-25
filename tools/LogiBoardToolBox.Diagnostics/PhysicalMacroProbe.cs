using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

internal sealed record MacroObservation(double Milliseconds, string Source, string Kind, int Code, bool Down, bool Injected, string Device, string Raw = "");

/// <summary>Bounded native input observation, with only the expected macro outputs swallowed. Never generates input.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class PhysicalMacroProbe : IDisposable
{
    private readonly ConcurrentQueue<MacroObservation> events = new();
    private readonly Dictionary<IntPtr, string> paths = [];
    private readonly Thread thread;
    private readonly WndProc windowCallback;
    private readonly HookProc mouseCallback, keyboardCallback;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch time = Stopwatch.StartNew();
    private readonly string target;
    private readonly bool interceptExpectedOutput;
    private readonly int? diagnosticGuardKey;
    private readonly int? passThroughCalibrationKey;
    private readonly bool observeDeviceChanges;
    private long armedUntil;
    private uint threadId;
    private IntPtr window, mouseHook, keyboardHook;
    private bool disposed;
    public Task Ready => ready.Task;
    public bool Armed => Stopwatch.GetTimestamp() < Interlocked.Read(ref armedUntil);
    public IReadOnlyList<MacroObservation> Events => events.ToArray();
    private static bool ExpectedKey(int key) => key is 0xDC or 0x31; // Hook blocks only the two expected macro keys.

    public PhysicalMacroProbe(ushort productId, bool interceptExpectedOutput = true, int? diagnosticGuardKey = null, int? passThroughCalibrationKey = null, bool observeDeviceChanges = false)
    {
        if (diagnosticGuardKey.HasValue && (interceptExpectedOutput || diagnosticGuardKey is not (0x87 or 0x77 or 0x70)))
            throw new ArgumentException("独立诊断只允许保护指定的功能测试键，不能同时拦截普通宏输出。", nameof(diagnosticGuardKey));
        if (passThroughCalibrationKey.HasValue && (passThroughCalibrationKey != 0x31 || interceptExpectedOutput || diagnosticGuardKey.HasValue))
            throw new ArgumentException("透传校准只允许独立观察数字 1，不得拦截输入。", nameof(passThroughCalibrationKey));
        this.interceptExpectedOutput = interceptExpectedOutput;
        this.diagnosticGuardKey = diagnosticGuardKey;
        this.passThroughCalibrationKey = passThroughCalibrationKey;
        this.observeDeviceChanges = observeDeviceChanges;
        target = $"VID_046D&PID_{productId:X4}";
        windowCallback = WindowMessage; mouseCallback = MouseMessage; keyboardCallback = KeyboardMessage;
        thread = new(Run) { IsBackground = true, Name = "Bounded native onboard macro observation" }; thread.Start();
    }
    public void Arm(TimeSpan duration)
    {
        var maximum = diagnosticGuardKey.HasValue ? TimeSpan.FromMilliseconds(500)
            : interceptExpectedOutput ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(30);
        if (duration <= TimeSpan.Zero || duration > maximum) throw new ArgumentOutOfRangeException(nameof(duration));
        while (events.TryDequeue(out _)) { }
        time.Restart(); Interlocked.Exchange(ref armedUntil, Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency));
    }
    private void Add(string source, string kind, int code, bool down, bool injected, string path, string raw = "")
    {
        if (events.Count < 100000) events.Enqueue(new(time.Elapsed.TotalMilliseconds, source, kind, code, down, injected, path, raw));
    }
    private IntPtr MouseMessage(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && Armed && message.ToInt64() is 0x201 or 0x202)
        {
            Add("low-level-hook", "mouse", 1, message.ToInt64() == 0x201, (Marshal.ReadInt32(data, 12) & 3) != 0, "");
            return new(1);
        }
        return CallNextHookEx(mouseHook, code, message, data);
    }
    private IntPtr KeyboardMessage(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && Armed && passThroughCalibrationKey.HasValue && Marshal.ReadInt32(data) == passThroughCalibrationKey)
        {
            var flags = Marshal.ReadInt32(data, 8);
            Add("calibration-hook", "keyboard", Marshal.ReadInt32(data), (flags & 0x80) == 0, (flags & 0x12) != 0, "");
            return CallNextHookEx(keyboardHook, code, message, data);
        }
        if (code >= 0 && Armed && (diagnosticGuardKey.HasValue
            ? Marshal.ReadInt32(data) == diagnosticGuardKey.Value
            : interceptExpectedOutput && ExpectedKey(Marshal.ReadInt32(data))))
        {
            var flags = Marshal.ReadInt32(data, 8);
            Add("low-level-hook", "keyboard", Marshal.ReadInt32(data), (flags & 0x80) == 0, (flags & 0x12) != 0, "");
            return new(1);
        }
        return CallNextHookEx(keyboardHook, code, message, data);
    }
    private IntPtr WindowMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam)
    {
        if (observeDeviceChanges && message == 0xFE)
        {
            if (!paths.TryGetValue(lparam, out var path)) path = DevicePath(lparam);
            if (Armed && path.Contains(target, StringComparison.OrdinalIgnoreCase))
                Add("device-notification", wparam.ToInt64() == 1 ? "arrival" : "removal", 0, false, false, path);
            if (wparam.ToInt64() == 2) paths.Remove(lparam); else paths[lparam] = path;
        }
        if (message == 0xFF && Armed)
        {
            uint size = 0; var header = (uint)(IntPtr.Size == 8 ? 24 : 16);
            if (GetRawInputData(lparam, 0x10000003, IntPtr.Zero, ref size, header) == 0 && size is >= 32 and <= 1024)
            {
                var data = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (GetRawInputData(lparam, 0x10000003, data, ref size, header) == size)
                    {
                        var type = Marshal.ReadInt32(data); var device = Marshal.ReadIntPtr(data, 8);
                        if (!paths.TryGetValue(device, out var path)) paths[device] = path = DevicePath(device);
                        if (path.Contains(target, StringComparison.OrdinalIgnoreCase))
                        {
                            var payload = data + (int)header;
                            if ((interceptExpectedOutput || diagnosticGuardKey.HasValue) && type == 0 && size >= header + 24)
                            {
                                var flags = (ushort)Marshal.ReadInt16(payload, 4);
                                if ((flags & 1) != 0) Add("raw-input", "mouse", 1, true, false, path);
                                if ((flags & 2) != 0) Add("raw-input", "mouse", 1, false, false, path);
                                if (diagnosticGuardKey.HasValue)
                                {
                                    var raw = new byte[24]; Marshal.Copy(payload, raw, 0, raw.Length);
                                    Add("raw-input", "mouse-report", flags, false, false, path, Convert.ToHexString(raw));
                                }
                            }
                            if (type == 1 && size >= header + 16)
                            {
                                var key = (ushort)Marshal.ReadInt16(payload, 6);
                                // Read every code from this mouse receiver, so an incorrectly translated
                                // usage is visible. Input from other keyboards is never collected here.
                                var raw = new byte[16]; Marshal.Copy(payload, raw, 0, raw.Length);
                                Add("raw-input", "keyboard", key, (Marshal.ReadInt16(payload, 2) & 1) == 0, false, path, Convert.ToHexString(raw));
                            }
                            if (!interceptExpectedOutput && type == 2 && size >= header + 8)
                            {
                                var reportSize = Marshal.ReadInt32(payload); var reportCount = Marshal.ReadInt32(payload, 4);
                                var bytes = (long)reportSize * reportCount;
                                if (reportSize > 0 && reportCount > 0 && bytes <= size - header - 8 && bytes <= 512)
                                {
                                    var raw = new byte[(int)bytes]; Marshal.Copy(payload + 8, raw, 0, raw.Length);
                                    Add("raw-input", "hid", raw[0], raw.Skip(1).Any(b => b != 0), false, path, Convert.ToHexString(raw));
                                }
                            }
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(data); }
            }
        }
        return DefWindowProc(hwnd, message, wparam, lparam);
    }
    private void Run()
    {
        var name = "LogiBoardMacroProbe-" + Guid.NewGuid().ToString("N"); var module = GetModuleHandle(null);
        try
        {
            threadId = GetCurrentThreadId();
            var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Callback = windowCallback, Instance = module, Name = name };
            if (RegisterClassEx(ref cls) == 0) throw new Win32Exception();
            window = CreateWindowEx(0, name, "", 0, 0, 0, 0, 0, new(-3), IntPtr.Zero, module, IntPtr.Zero);
            if (window == IntPtr.Zero) throw new Win32Exception();
            RawDevice[] devices = interceptExpectedOutput
                ? [new() { Page = 1, Usage = 2, Flags = 0x100, Target = window }, new() { Page = 1, Usage = 6, Flags = 0x100, Target = window }]
                : [new() { Page = 1, Usage = 6, Flags = 0x100, Target = window },
                   new() { Page = 12, Usage = 1, Flags = 0x100, Target = window },
                   new() { Page = 1, Usage = 0x80, Flags = 0x100, Target = window }];
            if (diagnosticGuardKey.HasValue)
                devices = [.. devices, new() { Page = 1, Usage = 2, Flags = 0x100, Target = window }];
            if (observeDeviceChanges)
                for (var i = 0; i < devices.Length; i++) devices[i].Flags |= 0x2000;
            if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawDevice>())) throw new Win32Exception();
            if (observeDeviceChanges)
            {
                uint count = 0; var size = (uint)Marshal.SizeOf<RawDeviceList>();
                if (GetRawInputDeviceList(null, ref count, size) == 0 && count is > 0 and <= 1024)
                {
                    var list = new RawDeviceList[count];
                    if (GetRawInputDeviceList(list, ref count, size) != uint.MaxValue)
                        foreach (var item in list) paths[item.Device] = DevicePath(item.Device);
                }
            }
            if (interceptExpectedOutput)
            {
                mouseHook = SetWindowsHookEx(14, mouseCallback, module, 0); keyboardHook = SetWindowsHookEx(13, keyboardCallback, module, 0);
                if (mouseHook == IntPtr.Zero || keyboardHook == IntPtr.Zero) throw new Win32Exception();
            }
            else if (diagnosticGuardKey.HasValue || passThroughCalibrationKey.HasValue)
            {
                keyboardHook = SetWindowsHookEx(13, keyboardCallback, module, 0);
                if (keyboardHook == IntPtr.Zero) throw new Win32Exception();
            }
            ready.TrySetResult();
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
        }
        catch (Exception error) { ready.TrySetException(error); }
        finally
        {
            if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
            if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
            if (window != IntPtr.Zero) DestroyWindow(window);
            UnregisterClass(name, module); GC.KeepAlive(windowCallback); GC.KeepAlive(mouseCallback); GC.KeepAlive(keyboardCallback);
        }
    }
    private static string DevicePath(IntPtr device)
    {
        uint size = 0; GetRawInputDeviceInfo(device, 0x20000007, null, ref size);
        if (size == 0 || size > 32768) return "";
        var value = new StringBuilder((int)size + 1);
        return GetRawInputDeviceInfo(device, 0x20000007, value, ref size) == uint.MaxValue ? "" : value.ToString();
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; Interlocked.Exchange(ref armedUntil, 0);
        if (threadId != 0) PostThreadMessage(threadId, 0x12, IntPtr.Zero, IntPtr.Zero);
        thread.Join(1500);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass { public uint Size, Style; public WndProc Callback; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background; public string? Menu, Name; public IntPtr SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct RawDevice { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] private struct RawDeviceList { public IntPtr Device; public uint Type; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public IntPtr Window; public uint Id; public IntPtr Wparam, Lparam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, IntPtr module);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr module, IntPtr data);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr raw, uint command, IntPtr data, ref uint size, uint header);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputDeviceList([Out] RawDeviceList[]? list, ref uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder? data, ref uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
