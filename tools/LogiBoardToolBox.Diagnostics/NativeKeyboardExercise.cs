using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Core.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class NativeKeyboardExercise
{
    public static async Task RunAsync(LogitechDevice device, string referencePath, string folder)
    {
        Directory.CreateDirectory(folder);
        if (File.Exists(Path.Combine(folder, "start.signal"))) throw new IOException("需要新的测试目录。");
        using var client = LogitechDiscovery.Open(device);
        var memory = new OnboardMemory(client, device.Identity, device.Name);
        var before = await memory.ReadAsync(); await OnboardMemory.SaveBackupAsync(before, Path.Combine(folder, "before.json"));
        var native = await OnboardMemory.LoadBackupAsync(referencePath);
        var profile = native.GetProfiles().Single(p => p.Sector == 3).Copy();
        var binding = profile.GetBinding(5);
        var expected = Convert.FromHexString("43003140003B4400314000C643001E40001944001E40000AFF");
        if (!ButtonBinding.IsMacro(binding) || !MacroAllocator.ReadMacroBytes(native, new(ButtonBinding.MacroSector(binding), ButtonBinding.MacroOffset(binding))).SequenceEqual(expected))
            throw new InvalidDataException("参考文件未包含 G HUB 编译的 G6 单次取消刀。");
        profile.Enabled = true;
        var enable = MemoryWritePlan.Create(native, profile, [], true);
        foreach (var change in enable.Changes) native.Sectors[change.Sector] = change.After;
        native.Mode = 1; native.ActiveSector = 3;
        var plan = MemoryWritePlan.Restore(before, native, true);
        using var probe = new PhysicalMacroProbe(device.Hid.ProductId); await probe.Ready;
        var spy = await client.GetFeatureAsync(0x8110);
        var physical = new ConcurrentQueue<object>(); var timer = new Stopwatch();
        void Notification(object? sender, HidNotification n)
        {
            if (probe.Armed && n.FeatureIndex == spy && n.Function == 0 && n.Data.Length >= 2)
                physical.Enqueue(new { Milliseconds = timer.Elapsed.TotalMilliseconds, Mask = (n.Data[0] << 8) | n.Data[1], Hex = Convert.ToHexString(n.Data) });
        }
        await Save("prepared.json", new { device.Identity, ReferencePath = referencePath, Source = "G HUB native copy", TargetSector = 3, ButtonIndex = 5, Bytecode = Convert.ToHexString(expected), RestoreAllSectorsAtEnd = true, Changes = plan.Changes.Select(c => new { c.Sector, c.Purpose }) });
        Console.WriteLine("G HUB 原生键盘对照已准备。等待开始信号；结束后恢复全部原配置。");
        var deadline = DateTime.UtcNow.AddMinutes(15);
        while (!File.Exists(Path.Combine(folder, "start.signal"))) { if (DateTime.UtcNow > deadline) return; await Task.Delay(100); }
        var installed = false; var spying = false;
        try
        {
            await memory.CommitAsync(plan, Path.Combine(folder, "backups")); installed = true;
            await OnboardMemory.SaveBackupAsync(await memory.ReadAsync(), Path.Combine(folder, "installed.json"));
            client.Notification += Notification;
            if (spy != 0) { await client.FeatureAsync(0x8110, 1); spying = true; }
            timer.Start(); probe.Arm(TimeSpan.FromSeconds(30));
            await Save("armed.json", new { ArmedAt = DateTimeOffset.Now, Seconds = 30, NoSoftwareMacroEngine = true, PhysicalButtonObservation = spying });
            Console.WriteLine("ARMED：先按键盘数字 1 一次，然后单按 G6 一次。结束后自动恢复。");
            while (probe.Armed) await Task.Delay(100);
            await Save("observations.json", new { Events = probe.Events, PhysicalButtons = physical.ToArray(), ReceiverKeyboardEvents = probe.Events.Count(e => e.Source == "raw-input" && e.Kind == "keyboard"), InjectedEvents = probe.Events.Count(e => e.Injected) });
        }
        finally
        {
            client.Notification -= Notification;
            try { if (spying) await client.FeatureAsync(0x8110, 2); }
            finally
            {
                if (installed)
                {
                    var restore = MemoryWritePlan.Restore(await memory.ReadAsync(), before, true);
                    if (restore.Changes.Count != 0) await memory.CommitAsync(restore, Path.Combine(folder, "backups"));
                    if (before.Mode == 1) await memory.ActivateProfileAsync(before.ActiveSector); else await memory.SetModeAsync(false);
                    var after = await memory.ReadAsync(); await OnboardMemory.SaveBackupAsync(after, Path.Combine(folder, "restored.json"));
                    var same = before.Sectors.All(p => after.Sectors[p.Key].SequenceEqual(p.Value)) && before.Mode == after.Mode && before.ActiveSector == after.ActiveSector;
                    await Save("restoration.json", new { AllSectorsModeAndActiveIdentical = same });
                    if (!same) throw new IOException("原生键盘对照恢复校验失败。");
                }
            }
        }
        Console.WriteLine("对照结束，已全量恢复原配置。");
        Task Save(string name, object value) => File.WriteAllTextAsync(Path.Combine(folder, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
