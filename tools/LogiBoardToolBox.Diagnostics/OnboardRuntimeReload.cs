using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Core.Windows;

/// <summary>One idle-only runtime recovery. Does not write flash, install hooks or generate input.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class OnboardRuntimeReload
{
    public static async Task RunAsync(LogitechDevice device, string folder)
    {
        if (device.Identity != "046D:C547:C972BA47:01") throw new InvalidOperationException("运行状态恢复仅用于已核对的目标鼠标。");
        Directory.CreateDirectory(folder);
        if (File.Exists(Path.Combine(folder, "started.json"))) throw new IOException("此恢复目录已使用，禁止重复执行。");
        using var client = LogitechDiscovery.Open(device);
        var memory = new OnboardMemory(client, device.Identity, device.Name);
        var before = await memory.ReadAsync();
        if (before.ReadErrors.Count != 0 || before.Mode != 1 || before.ActiveSector != 1) throw new InvalidOperationException("只在当前板载配置 1 完整可读时恢复。");
        var performance = new PerformanceController(client, true);
        var settings = await performance.ReadAsync();
        var profile = before.GetProfiles().Single(p => p.Sector == before.ActiveSector);
        if (profile.Dpis.Count(d => d != 0) != 1 || profile.Dpis[profile.DefaultDpiIndex] != settings.CurrentX
            || profile.GetDpisY(before.Capabilities)[profile.DefaultDpiIndex] != settings.CurrentY)
            throw new InvalidOperationException("当前 DPI 与唯一板载档位不一致，停止重载以免改变灵敏度。");
        await OnboardMemory.SaveBackupAsync(before, Path.Combine(folder, "before.json"));
        _ = await client.GetFeatureAsync(0x8110); // Resolve indices before the short mode transition.
        var trace = new ConcurrentQueue<string>(); client.Trace = trace.Enqueue;
        Console.WriteLine("等待输入空闲；不会拦截或产生输入。最多等待 60 秒，忙碌时不执行。");
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (IdleMilliseconds() < 5000 || MouseButtonHeld())
        {
            if (DateTime.UtcNow >= deadline)
            {
                await Save("skipped.json", new { Reason = "No idle interval", NoStateChanged = true }); return;
            }
            await Task.Delay(250);
        }
        var current = await memory.ReadAsync();
        if (current.Mode != before.Mode || current.ActiveSector != before.ActiveSector
            || before.Sectors.Any(p => !p.Value.SequenceEqual(current.Sectors[p.Key]))
            || IdleMilliseconds() < 5000 || MouseButtonHeld())
        {
            await Save("skipped.json", new { Reason = "State or input changed before transition", NoStateChanged = true }); return;
        }
        await Save("started.json", new { At = DateTimeOffset.Now, IdleMs = IdleMilliseconds(), FlashWrites = false, InputGenerated = false, Hooks = false, RestoreMode = before.Mode, RestoreSector = before.ActiveSector });
        if (IdleMilliseconds() < 5000 || MouseButtonHeld())
        {
            await Save("skipped.json", new { Reason = "Input resumed", NoStateChanged = true }); return;
        }
        var timer = Stopwatch.StartNew();
        try
        {
            if (await client.GetFeatureAsync(0x8110) != 0) await client.FeatureAsync(0x8110, 2);
            await client.FeatureAsync(0x8100, 1, [2]);
        }
        finally
        {
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            // Also restore after a timed-out mode request: the mouse may have
            // accepted it even when its acknowledgement was lost.
            await client.FeatureAsync(0x8100, 1, [1], recovery.Token);
            await client.FeatureAsync(0x8100, 3, [(byte)(before.ActiveSector >> 8), (byte)before.ActiveSector], recovery.Token);
        }
        timer.Stop();
        var read = await performance.ReadAsync();
        if (read.CurrentX != settings.CurrentX || read.CurrentY != settings.CurrentY)
            await performance.SetDpiAsync(settings.CurrentX, settings.CurrentY, settings);
        var after = await memory.ReadAsync();
        var final = await performance.ReadAsync();
        await OnboardMemory.SaveBackupAsync(after, Path.Combine(folder, "after.json"));
        var identical = after.ReadErrors.Count == 0 && before.Sectors.All(p => p.Value.SequenceEqual(after.Sectors[p.Key]))
            && before.Mode == after.Mode && before.ActiveSector == after.ActiveSector
            && settings.CurrentX == final.CurrentX && settings.CurrentY == final.CurrentY
            && settings.WiredRate == final.WiredRate && settings.WirelessRate == final.WirelessRate;
        await File.WriteAllLinesAsync(Path.Combine(folder, "trace.log"), trace);
        await Save("completed.json", new { At = DateTimeOffset.Now, TransitionMs = timer.Elapsed.TotalMilliseconds,
            AllSectorsModeDpiAndRatesPreserved = identical, after.Mode, after.ActiveSector, final.CurrentX, final.CurrentY,
            FlashWrites = false, InputGenerated = false, Hooks = false, ForegroundActivation = false,
            HardwareExecutionVerified = false });
        if (!identical) throw new IOException("运行状态重载后的配置或性能核对未通过。");
        Console.WriteLine($"运行状态已重载；全部扇区、板载模式、活动槽和 DPI 保持一致。切换耗时 {timer.Elapsed.TotalMilliseconds:F1} ms；尚未验证宏输出。");
        Task Save(string name, object value) => File.WriteAllTextAsync(Path.Combine(folder, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static uint IdleMilliseconds()
    {
        var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref input)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return unchecked((uint)Environment.TickCount - input.Time);
    }
    private static bool MouseButtonHeld() => new[] { 1, 2, 4, 5, 6 }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Time; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetLastInputInfo(ref LastInput data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
