using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LogiBoardToolBox.Core.Macros;

namespace LogiBoardToolBox.Core.Windows;

/// <summary>Explicitly enabled, physical mouse events only; never intercepts normal clicks.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsLuaSession : IAsyncDisposable
{
    private readonly LuaMacroRuntime runtime;
    private readonly HookCallback callback;
    private readonly bool left, right;
    private IntPtr hook;
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    public event EventHandler<Exception>? Faulted;
    public event EventHandler<string>? Logged;
    public WindowsLuaSession(bool executeLeft, bool executeRight)
    {
        left = executeLeft; right = executeRight;
        runtime = new(new WindowsInputSink(), ReadKeyState);
        runtime.Faulted += (_, error) => Faulted?.Invoke(this, error);
        runtime.Logged += (_, line) => Logged?.Invoke(this, line);
        callback = Mouse;
    }
    public async Task StartAsync(string source)
    {
        if (hook != IntPtr.Zero) throw new InvalidOperationException("Lua 已启动。");
        ObjectDisposedException.ThrowIf(disposed, this);
        var ct = lifetime.Token;
        await runtime.StartAsync(source, ct);
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            await runtime.StopAsync();
            throw new System.ComponentModel.Win32Exception(error, "无法监听 Lua 触发键。");
        }
    }
    private IntPtr Mouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            try
            {
                var input = Marshal.PtrToStructure<MouseData>(data);
                if ((input.Flags & 3) == 0)
                {
                    var msg = message.ToInt64();
                    var button = msg switch { 0x201 or 0x202 => 1, 0x204 or 0x205 => 3, 0x207 or 0x208 => 2, 0x20B or 0x20C => 3 + (int)(input.Data >> 16), _ => 0 };
                    if (button != 0)
                        runtime.MouseButton(button, msg is 0x201 or 0x204 or 0x207 or 0x20B, button switch { 1 => left, 3 => right, _ => true });
                }
            }
            catch (Exception error) { try { Faulted?.Invoke(this, error); } catch { /* Never unwind through the native hook. */ } }
        }
        return CallNextHookEx(hook, code, message, data);
    }
    private static bool ReadKeyState(string key)
    {
        var vk = key.ToLowerInvariant() switch
        {
            "capslock" => 0x14, "numlock" => 0x90, "scrolllock" => 0x91,
            "lctrl" => 0xA2, "rctrl" => 0xA3, "ctrl" => 0x11,
            "lshift" => 0xA0, "rshift" => 0xA1, "shift" => 0x10,
            "lalt" => 0xA4, "ralt" => 0xA5, "alt" => 0x12,
            _ => throw new NotSupportedException("不支持的状态键：" + key)
        };
        return key is "capslock" or "numlock" or "scrolllock" ? (GetKeyState(vk) & 1) != 0 : (GetAsyncKeyState(vk) & 0x8000) != 0;
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true;
        lifetime.Cancel();
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        await runtime.DisposeAsync();
        lifetime.Dispose();
    }
    private delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public Point Point; public uint Data, Flags, Time; public UIntPtr ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int kind, HookCallback callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
}
