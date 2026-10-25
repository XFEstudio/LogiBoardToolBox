using System.Text.Json;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Core.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class WatchOnboardOutput
{
    public static async Task RunAsync(LogitechDevice device, string folder)
    {
        Directory.CreateDirectory(folder);
        using var client = LogitechDiscovery.Open(device);
        var memory = new OnboardMemory(client, device.Identity, device.Name);
        var before = await memory.ReadAsync(); await OnboardMemory.SaveBackupAsync(before, Path.Combine(folder, "before.json"));
        if (before.Mode != 1) throw new InvalidOperationException("只在当前已处于板载模式时观察，避免干扰用户的工作模式。");
        var trace = new List<string>(); client.Trace = trace.Add;
        var state = await memory.SetModeAsync(true); client.Trace = null;
        await File.WriteAllLinesAsync(Path.Combine(folder, "transition-trace.log"), trace);
        if (state.Mode != before.Mode || state.ActiveSector != before.ActiveSector) throw new IOException("清理设备监听时活动配置发生了变化。");
        using var probe = new PhysicalMacroProbe(device.Hid.ProductId, interceptExpectedOutput: false); await probe.Ready;
        probe.Arm(TimeSpan.FromMinutes(3));
        await Save("armed.json", new { ArmedAt = DateTimeOffset.Now, Seconds = 180, KeyboardHooks = false, MouseHooks = false, InputGenerated = false, FlashWrites = false, BindingsChanged = false, Target = "C547 receiver only; all keyboard usages, including unexpected translated codes" });
        Console.WriteLine("后台观察已启动：无输入钩子、不截键、不模拟输入、不改键、不写入闪存。无需配合。");
        while (probe.Armed)
        {
            await Task.Delay(2000);
            await Save("progress.json", new { UpdatedAt = DateTimeOffset.Now, Events = probe.Events.Count, BackslashDown = probe.Events.Count(e => e.Code == 0xDC && e.Down), Digit1Down = probe.Events.Count(e => e.Code == 0x31 && e.Down) });
        }
        var events = probe.Events;
        var cycles = new List<object>();
        for (var i = 0; i + 3 < events.Count; i++)
        {
            var block = events.Skip(i).Take(4).ToArray();
            if (block[0].Code == 0xDC && block[0].Down && block[1].Code == 0xDC && !block[1].Down && block[2].Code == 0x31 && block[2].Down && block[3].Code == 0x31 && !block[3].Down)
                cycles.Add(new { At = block[0].Milliseconds, Delays = new[] { block[1].Milliseconds - block[0].Milliseconds, block[2].Milliseconds - block[1].Milliseconds, block[3].Milliseconds - block[2].Milliseconds }, Injected = block.Any(e => e.Injected) });
        }
        var after = await memory.ReadAsync(); await OnboardMemory.SaveBackupAsync(after, Path.Combine(folder, "after.json"));
        await Save("completed.json", new { Events = events, CancelCycles = cycles, ReceiverKeyboardEvents = events.Count, AllSectorsUnchanged = before.Sectors.All(p => after.Sectors[p.Key].SequenceEqual(p.Value)), after.Mode, after.ActiveSector, HardwareExecutionObserved = cycles.Count != 0 });
        Console.WriteLine($"后台观察结束，收到 {events.Count} 个目标接收器键盘事件，{cycles.Count} 组取消刀序列。");
        Task Save(string name, object value) => File.WriteAllTextAsync(Path.Combine(folder, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
