using System.Text.Json;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Core.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class ImportedMacroObservation
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static async Task RunAsync(LogitechDevice device, string settings, string folder)
    {
        Directory.CreateDirectory(folder);
        if (File.Exists(Path.Combine(folder, "start.signal"))) throw new IOException("请使用新的实测目录，不能复用开始信号。");
        var import = GHubMacroImport.Read(await File.ReadAllTextAsync(settings));
        using var client = LogitechDiscovery.Open(device);
        var memory = new OnboardMemory(client, device.Identity, device.Name); var before = await memory.ReadAsync();
        var profile = before.GetProfiles().Single(p => p.Sector == before.ActiveSector && p.Enabled);
        if (before.Mode != 1) throw new IOException("需要保持板载模式进行原生事件观察。");
        foreach (var macro in import.Bindings)
        {
            var binding = profile.GetBinding(macro.ButtonIndex, macro.GShift);
            if (!ButtonBinding.IsMacro(binding) || !MacroAllocator.ReadMacroBytes(before, new(ButtonBinding.MacroSector(binding), ButtonBinding.MacroOffset(binding))).SequenceEqual(macro.Compiled.Bytes))
                throw new IOException($"{macro.SlotId} 的板载内容尚未与导入源一致。");
        }
        await OnboardMemory.SaveBackupAsync(before, Path.Combine(folder, "before.json"));
        using var probe = new PhysicalMacroProbe(device.Hid.ProductId); await probe.Ready;
        await Save("prepared.json", new { device.Identity, before.Mode, before.ActiveSector, NoFlashWrites = true, NoInputGenerated = true, Filter = "C547 raw input; left mouse button, backslash and 1 only", Expected = import.Bindings.Select(b => new { b.SlotId, b.ButtonIndex, b.Name, b.RecordedDelays }) });
        Console.WriteLine("原生实测已准备；没有写入或生成输入，等待 start.signal。");
        var deadline = DateTime.UtcNow.AddMinutes(15);
        while (!File.Exists(Path.Combine(folder, "start.signal")))
        {
            if (DateTime.UtcNow >= deadline) { Console.WriteLine("实测未开始，退出。"); return; }
            await Task.Delay(200);
        }
        var fresh = await memory.ReadAsync();
        if (fresh.Mode != before.Mode || fresh.ActiveSector != before.ActiveSector || before.Sectors.Any(p => !p.Value.SequenceEqual(fresh.Sectors[p.Key]))) throw new IOException("等待期间设备设置变化，请重新准备。");
        probe.Arm(TimeSpan.FromSeconds(30));
        await Save("armed.json", new { ArmedAt = DateTimeOffset.Now, Seconds = 30, TemporarilyBlocked = "Left clicks, backslash and 1", SoftwareMacroEngine = false });
        Console.WriteLine("ARMED：请先按住原 G HUB 取消刀键 1 秒并松开，等 2 秒后按住原 G HUB 左键连点键 1 秒并松开。");
        while (probe.Armed) await Task.Delay(100);
        var events = probe.Events;
        var keys = events.Where(e => e.Source == "raw-input" && e.Kind == "keyboard").ToArray();
        var clicks = events.Where(e => e.Source == "raw-input" && e.Kind == "mouse").ToArray();
        var cancelCycles = new List<object>();
        for (var i = 0; i + 3 < keys.Length; i++)
        {
            var cycle = keys.Skip(i).Take(4).ToArray();
            if (!cycle.Select(e => (e.Code, e.Down)).SequenceEqual(new[] { (0xDC, true), (0xDC, false), (0x31, true), (0x31, false) })) continue;
            var gaps = cycle.Skip(1).Select((e, j) => e.Milliseconds - cycle[j].Milliseconds).ToArray();
            cancelCycles.Add(new { StartMilliseconds = cycle[0].Milliseconds, Gaps = gaps, RecordedDelayMatchWithin15ms = gaps.Zip(new[] { 59, 198, 25 }).All(g => Math.Abs(g.First - g.Second) <= 15) });
        }
        var after = await memory.ReadAsync();
        await Save("observations.json", new
        {
            NativeKeyboardSequenceObserved = cancelCycles.Count != 0,
            NativeKeyboardRepeatsObserved = cancelCycles.Count >= 2,
            NativeLeftClickRepeatsObserved = clicks.Count(e => e.Down) >= 2 && clicks.Count(e => !e.Down) >= 2,
            KeyboardEvents = keys.Length, MouseEvents = clicks.Length, CancelCycles = cancelCycles,
            InjectedHookEvents = events.Count(e => e.Injected), Events = events,
            AllSectorsUnchanged = before.Sectors.All(p => p.Value.SequenceEqual(after.Sectors[p.Key])),
            ModeUnchanged = before.Mode == after.Mode && before.ActiveSector == after.ActiveSector,
            PhysicalTriggerIdentity = "Requires user's specified physical button order; inferred from output, not a generated press",
            InGameEffect = "Not tested by native output observation"
        });
        await OnboardMemory.SaveBackupAsync(after, Path.Combine(folder, "after.json"));
        Console.WriteLine($"实测结束：接收器键盘事件 {keys.Length}，左键事件 {clicks.Length}；取消刀序列 {cancelCycles.Count} 轮。临时拦截已自动结束。");
        Task Save(string name, object value) => File.WriteAllTextAsync(Path.Combine(folder, name), JsonSerializer.Serialize(value, Json));
    }
}
