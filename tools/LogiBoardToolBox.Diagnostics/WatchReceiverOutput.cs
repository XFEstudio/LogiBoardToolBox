using System.Collections.Concurrent;
using System.Text.Json;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Core.Windows;

/// <summary>Passive, device-scoped observation. No mode, map, spy or flash writes.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class WatchReceiverOutput
{
    public static async Task RunAsync(LogitechDevice device, string folder)
    {
        if (device.Identity != "046D:C547:C972BA47:01") throw new InvalidOperationException("只观察已核对的目标鼠标。");
        if (WindowsHidTransport.EnumerateLogitechDevices().Where(d => d.ProductId == device.Hid.ProductId).Select(d => d.CollectionGroup).Distinct().Count() != 1)
            throw new InvalidOperationException("存在多个同型号接收器，停止以免采集其他设备。");
        Directory.CreateDirectory(folder);
        if (File.Exists(Path.Combine(folder, "armed.json"))) throw new IOException("观察目录已经使用。");
        using var client = LogitechDiscovery.Open(device);
        var memory = new OnboardMemory(client, device.Identity, device.Name);
        var before = await memory.ReadAsync();
        await OnboardMemory.SaveBackupAsync(before, Path.Combine(folder, "before.json"));
        var spy = await client.GetFeatureAsync(0x8110); var onboard = await client.GetFeatureAsync(0x8100);
        var notifications = new ConcurrentQueue<object>();
        client.Notification += (_, n) =>
        {
            if (n.FeatureIndex == spy || n.FeatureIndex == onboard)
                notifications.Enqueue(new { At = DateTimeOffset.Now, Feature = n.FeatureIndex, n.Function, Data = Convert.ToHexString(n.Data) });
        };
        using var probe = new PhysicalMacroProbe(device.Hid.ProductId, interceptExpectedOutput: false);
        await probe.Ready; probe.Arm(TimeSpan.FromMinutes(30));
        await Save("armed.json", new { At = DateTimeOffset.Now, Seconds = 1800, device.Identity,
            KeyboardHooks = false, MouseHooks = false, InputGenerated = false, ModeChanged = false,
            SpyChanged = false, ButtonMapChanged = false, FlashWrites = false,
            ObservedCollections = "Target receiver only: keyboard, consumer, system control",
            ExpectedKeyFilter = false, RawKeyboardPayloadIncluded = true });
        Console.WriteLine("接收器被动观察开始：只采集目标鼠标的键盘/媒体/系统控制输出；不改设备状态，不截键，不产生输入。无需配合。");
        var count = -1; var heartbeat = DateTime.UtcNow;
        while (probe.Armed)
        {
            await Task.Delay(2000);
            var events = probe.Events;
            if (count != events.Count)
            {
                count = events.Count;
                await Save("events.json", events);
                await Save("notifications.json", notifications.ToArray());
                Console.WriteLine($"目标接收器事件：{count}，最近：" + JsonSerializer.Serialize(events.TakeLast(6)));
            }
            if (DateTime.UtcNow >= heartbeat)
            {
                await Save("progress.json", new { At = DateTimeOffset.Now, EventCount = events.Count, Notifications = notifications.Count, KeyboardCodes = events.Where(e => e.Kind == "keyboard").Select(e => e.Code).Distinct().ToArray(), RemainingSeconds = Math.Max(0, 1800 - (DateTimeOffset.Now - File.GetCreationTimeUtc(Path.Combine(folder, "armed.json"))).TotalSeconds) });
                heartbeat = DateTime.UtcNow.AddSeconds(30);
            }
        }
        var after = await memory.ReadAsync();
        await OnboardMemory.SaveBackupAsync(after, Path.Combine(folder, "after.json"));
        await Save("completed.json", new { At = DateTimeOffset.Now, Events = probe.Events, Notifications = notifications.ToArray(),
            AllSectorsUnchanged = before.Sectors.All(p => after.Sectors.TryGetValue(p.Key, out var value) && p.Value.SequenceEqual(value)),
            OriginalMode = before.Mode, OriginalActive = before.ActiveSector, after.Mode, after.ActiveSector,
            KeyboardHooks = false, MouseHooks = false, InputGenerated = false, FlashWrites = false });
        Console.WriteLine($"接收器被动观察结束，共 {probe.Events.Count} 个目标事件。");
        Task Save(string name, object value) => File.WriteAllTextAsync(Path.Combine(folder, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
