using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LogiBoardToolBox.Core.Windows;

internal sealed record ClickObservation(double Milliseconds, string Source, bool Down, bool Injected, string Device);

/// <summary>Observes native reports. During a bounded test, left clicks are swallowed by a hook, never generated.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class PhysicalClickProbe : IDisposable
{
    private readonly ConcurrentQueue<ClickObservation> events = new();
    private readonly RawMouseMonitor monitor = new();
    private readonly Thread thread;
    private readonly HookCallback callback;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string target;
    private readonly Stopwatch time = Stopwatch.StartNew();
    private IntPtr hook;
    private uint threadId;
    private long armedUntil;
    public bool Armed => Stopwatch.GetTimestamp() < Interlocked.Read(ref armedUntil);
    public Task Ready => Task.WhenAll(monitor.Ready, ready.Task);
    public IReadOnlyList<ClickObservation> Events => events.ToArray();
    public PhysicalClickProbe(ushort productId)
    {
        target = $"VID_046D&PID_{productId:X4}";
        monitor.Packet += Observe;
        callback = Hook;
        thread = new(Run) { IsBackground = true, Name = "Bounded onboard macro click probe" }; thread.Start();
    }
    public void Arm(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(duration));
        while (events.TryDequeue(out _)) { }
        time.Restart(); Interlocked.Exchange(ref armedUntil, Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency));
    }
    private void Observe(object? sender, RawMousePacket packet)
    {
        if (!Armed || !packet.Path.Contains(target, StringComparison.OrdinalIgnoreCase)) return;
        if ((packet.Buttons & 1) != 0) events.Enqueue(new(time.Elapsed.TotalMilliseconds, "raw-input", true, false, packet.Path));
        if ((packet.Buttons & 2) != 0) events.Enqueue(new(time.Elapsed.TotalMilliseconds, "raw-input", false, false, packet.Path));
    }
    private IntPtr Hook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && Armed && message.ToInt64() is 0x0201 or 0x0202)
        {
            var flags = (uint)Marshal.ReadInt32(data, 12);
            events.Enqueue(new(time.Elapsed.TotalMilliseconds, "low-level-hook", message.ToInt64() == 0x0201, (flags & 3) != 0, ""));
            return new(1);
        }
        return CallNextHookEx(hook, code, message, data);
    }
    private void Run()
    {
        threadId = GetCurrentThreadId();
        hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) { ready.TrySetException(new System.ComponentModel.Win32Exception()); return; }
        try { ready.SetResult(); while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); } }
        finally { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; GC.KeepAlive(callback); }
    }
    public void Dispose() { Interlocked.Exchange(ref armedUntil, 0); monitor.Packet -= Observe; monitor.Dispose(); if (threadId != 0) PostThreadMessage(threadId, 0x12, IntPtr.Zero, IntPtr.Zero); thread.Join(1000); }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Message { public IntPtr Window; public uint Id; public IntPtr Wparam, Lparam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookCallback callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
}
