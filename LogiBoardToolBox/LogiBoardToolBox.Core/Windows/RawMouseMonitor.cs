using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace LogiBoardToolBox.Core.Windows;

public sealed record RawMouseDevice(IntPtr Handle, string Path);
public sealed record RawMousePacket(IntPtr Device, string Path, ushort Buttons, int X, int Y, bool Absolute, long Timestamp);

/// <summary>A message-only INPUTSINK window observes physical mouse reports without focus or injected input.</summary>
[SupportedOSPlatform("windows")]
public sealed class RawMouseMonitor : IDisposable
{
    private readonly Thread thread;
    private readonly WndProc callback;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<IntPtr, string> paths = [];
    private uint threadId;
    private IntPtr window;
    private bool disposed;
    public event EventHandler<RawMousePacket>? Packet;
    public Task Ready => ready.Task;
    public RawMouseMonitor()
    {
        callback = ProcessMessage;
        thread = new(Run) { IsBackground = true, Name = "Logitech physical mouse input monitor" };
        thread.Start();
    }
    public static IReadOnlyList<RawMouseDevice> Devices()
    {
        uint count = 0; var size = (uint)Marshal.SizeOf<DeviceList>();
        if (GetRawInputDeviceList(null, ref count, size) == uint.MaxValue) throw new Win32Exception();
        var list = new DeviceList[count];
        if (GetRawInputDeviceList(list, ref count, size) == uint.MaxValue) throw new Win32Exception();
        return list.Take((int)count).Where(d => d.Type == 0).Select(d => new RawMouseDevice(d.Device, DevicePath(d.Device))).ToArray();
    }
    private void Run()
    {
        var className = "LogiBoardRawInput-" + Guid.NewGuid().ToString("N"); var instance = GetModuleHandle(null);
        try
        {
            threadId = GetCurrentThreadId();
            var wc = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Callback = callback, Instance = instance, Name = className };
            if (RegisterClassEx(ref wc) == 0) throw new Win32Exception();
            window = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, new(-3), IntPtr.Zero, instance, IntPtr.Zero);
            if (window == IntPtr.Zero) throw new Win32Exception();
            if (!RegisterRawInputDevices([new() { Page = 1, Usage = 2, Flags = 0x100, Target = window }], 1, (uint)Marshal.SizeOf<RawDevice>())) throw new Win32Exception();
            ready.TrySetResult();
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
        }
        catch (Exception error) { ready.TrySetException(error); }
        finally
        {
            if (window != IntPtr.Zero) { DestroyWindow(window); window = IntPtr.Zero; }
            UnregisterClass(className, instance); GC.KeepAlive(callback);
        }
    }
    private IntPtr ProcessMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam)
    {
        if (message == 0x00FF)
        {
            uint size = 0; var headerSize = (uint)(IntPtr.Size == 8 ? 24 : 16);
            if (GetRawInputData(lparam, 0x10000003, IntPtr.Zero, ref size, headerSize) == 0 && size is >= 40 and <= 1024)
            {
                var data = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (GetRawInputData(lparam, 0x10000003, data, ref size, headerSize) == size && Marshal.ReadInt32(data) == 0)
                    {
                        var device = Marshal.ReadIntPtr(data, 8); var mouse = data + (int)headerSize;
                        if (!paths.TryGetValue(device, out var path)) paths[device] = path = DevicePath(device);
                        var packet = new RawMousePacket(device, path, (ushort)Marshal.ReadInt16(mouse, 4), Marshal.ReadInt32(mouse, 12), Marshal.ReadInt32(mouse, 16), (Marshal.ReadInt16(mouse) & 1) != 0, System.Diagnostics.Stopwatch.GetTimestamp());
                        try { Packet?.Invoke(this, packet); } catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
                    }
                }
                finally { Marshal.FreeHGlobal(data); }
            }
        }
        return DefWindowProc(hwnd, message, wparam, lparam);
    }
    private static string DevicePath(IntPtr device)
    {
        uint size = 0; GetRawInputDeviceInfo(device, 0x20000007, null, ref size);
        if (size == 0 || size > 32768) return "";
        var text = new StringBuilder((int)size + 1);
        return GetRawInputDeviceInfo(device, 0x20000007, text, ref size) == uint.MaxValue ? "" : text.ToString();
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (threadId != 0) PostThreadMessage(threadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != thread) thread.Join(1000);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass { public uint Size, Style; public WndProc Callback; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background; public string? Menu, Name; public IntPtr SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceList { public IntPtr Device; public uint Type; }
    [StructLayout(LayoutKind.Sequential)] private struct RawDevice { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public IntPtr Hwnd; public uint Id; public IntPtr Wparam, Lparam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr data);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessage(out Message message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr raw, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputDeviceList([In, Out] DeviceList[]? devices, ref uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder? data, ref uint size);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wparam, IntPtr lparam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
