using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LogiBoardToolBox.Services;

/// <summary>Observes clicks only while a reactive lighting session is enabled. It never injects input.</summary>
internal sealed class MouseActivityObserver : IDisposable
{
    private delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
    private readonly HookCallback callback;
    private IntPtr hook;
    public MouseActivityObserver(Action clicked)
    {
        callback = (code, message, data) =>
        {
            if (code >= 0 && message.ToInt64() is 0x0201 or 0x0204 or 0x0207 or 0x020B) clicked();
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        };
        hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法观察鼠标点击以触发涟漪灯效。");
    }
    public void Dispose() { if (hook == IntPtr.Zero) return; UnhookWindowsHookEx(hook); hook = IntPtr.Zero; GC.KeepAlive(callback); }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookCallback callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
