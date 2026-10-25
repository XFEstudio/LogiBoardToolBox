using System.Text.Json;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Core.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class HardwareExercises
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static async Task AdvancedAsync(LogitechDevice device, string folder)
    {
        Directory.CreateDirectory(folder);
        using var client = LogitechDiscovery.Open(device);
        var memory = new OnboardMemory(client, device.Identity, device.Name); var beforeFlash = await memory.ReadAsync();
        await OnboardMemory.SaveBackupAsync(beforeFlash, Path.Combine(folder, "before.json"));
        var controller = new AdvancedMouseController(client, device.Name); var before = await controller.ReadAsync();
        AdvancedMouseState? changedLightforce = null, changedStartup = null, restored = null;
        try
        {
            if (before.LightforceSupported) { await controller.SetLightforceAsync(!before.Hybrid); changedLightforce = controller.State; await controller.SetLightforceAsync(before.Hybrid); }
            if (before.StartupEnabled is { } startup) { await controller.SetStartupEffectAsync(!startup); changedStartup = controller.State; await controller.SetStartupEffectAsync(startup); }
        }
        finally
        {
            if (before.LightforceSupported) await controller.SetLightforceAsync(before.Hybrid);
            if (before.StartupEnabled is { } startup) await controller.SetStartupEffectAsync(startup);
            restored = await controller.ReadAsync(); var flash = await memory.ReadAsync();
            await File.WriteAllTextAsync(Path.Combine(folder, "advanced-readback.json"), JsonSerializer.Serialize(new { Before = before, ChangedLightforce = changedLightforce, ChangedStartup = changedStartup, Restored = restored, FlashUnchanged = beforeFlash.Sectors.All(p => p.Value.SequenceEqual(flash.Sectors[p.Key])), ModeUnchanged = beforeFlash.Mode == flash.Mode && beforeFlash.ActiveSector == flash.ActiveSector }, Json));
        }
    }
    public static async Task PrepareClickAsync(LogitechDevice device, string folder)
    {
        Directory.CreateDirectory(folder); using var client = LogitechDiscovery.Open(device);
        var memory = new OnboardMemory(client, device.Identity, device.Name); var before = await memory.ReadAsync();
        await OnboardMemory.SaveBackupAsync(before, Path.Combine(folder, "before.json"));
        var profile = before.GetProfiles().First(p => p.Enabled).Copy();
        var macro = MacroCompiler.Compile(string.Join('\n', Enumerable.Repeat("mouse 1 down\ndelay 20\nmouse 1 up\ndelay 80", 5)));
        var plan = MemoryWritePlan.Create(before, profile, [new(5, false, macro)], true);
        await File.WriteAllTextAsync(Path.Combine(folder, "plan.json"), JsonSerializer.Serialize(new { plan.Summary, plan.ActivateSector, TriggerPhysicalButtonIndex = 5, TriggerLabel = "G6 前进侧键", Clicks = 5, IntervalMilliseconds = 100, Bytecode = Convert.ToHexString(macro.Bytes), Changes = plan.Changes.Select(c => new { c.Sector, c.Purpose, Before = Convert.ToHexString(c.Before), After = Convert.ToHexString(c.After) }) }, Json));
        using var probe = new PhysicalClickProbe(device.Hid.ProductId); await probe.Ready;
        await File.WriteAllTextAsync(Path.Combine(folder, "prepared.json"), JsonSerializer.Serialize(new { device.Name, device.Identity, RawInputDevices = RawMouseMonitor.Devices().Select(d => new { Handle = d.Handle.ToInt64(), d.Path }), NoInputInjected = true, NoFlashWritten = true }, Json));
        Console.WriteLine("实测计划已生成；尚未写入。等待 start.signal（最长 15 分钟）。");
        var startSignal = Path.Combine(folder, "start.signal"); var deadline = DateTime.UtcNow.AddMinutes(15);
        while (!File.Exists(startSignal)) { if (DateTime.UtcNow >= deadline) { Console.WriteLine("未收到实测开始信号，无任何板载写入。"); return; } await Task.Delay(250); }
        var installed = false;
        try
        {
            await memory.CommitAsync(plan, Path.Combine(folder, "backups")); installed = true;
            var actual = await memory.ReadAsync(); await OnboardMemory.SaveBackupAsync(actual, Path.Combine(folder, "installed.json"));
            if (actual.Mode != 1 || plan.Changes.Any(c => !actual.Sectors[c.Sector].SequenceEqual(c.After))) throw new IOException("安装后的完整读回不一致。");
            // No HostMacroSession, MacroRuntime, SendInput, or software lighting runs in this process.
            probe.Arm(TimeSpan.FromSeconds(30));
            await File.WriteAllTextAsync(Path.Combine(folder, "armed.json"), JsonSerializer.Serialize(new { ArmedAt = DateTimeOffset.Now, Seconds = 30, NativeLeftClicksTemporarilySwallowed = true, SoftwareMacroEngine = false, Mode = actual.Mode }, Json));
            Console.WriteLine("ARMED：请在 30 秒内按一次 G6 前进侧键。测试期间左键事件会被临时截住。");
            while (probe.Armed) await Task.Delay(100);
            var events = probe.Events; var raw = events.Where(e => e.Source == "raw-input").ToArray();
            var downs = raw.Where(e => e.Down).ToArray(); var ups = raw.Where(e => !e.Down).ToArray();
            bool HasBurst(ClickObservation[] observations) => Enumerable.Range(0, Math.Max(0, observations.Length - 9)).Any(start =>
            {
                var burst = observations.Skip(start).Take(10).ToArray();
                return burst.Select((e, i) => e.Down == (i % 2 == 0)).All(v => v)
                    && Enumerable.Range(0, 5).All(i => burst[i * 2 + 1].Milliseconds - burst[i * 2].Milliseconds is >= 8 and <= 60)
                    && Enumerable.Range(0, 4).All(i => burst[(i + 1) * 2].Milliseconds - burst[i * 2].Milliseconds is >= 65 and <= 145);
            });
            var nativeHook = events.Where(e => e.Source == "low-level-hook" && !e.Injected).ToArray();
            await File.WriteAllTextAsync(Path.Combine(folder, "click-observations.json"), JsonSerializer.Serialize(new { PhysicalDeviceBurstObserved = HasBurst(raw), NonInjectedHookBurstObserved = HasBurst(nativeHook), RawDowns = downs.Length, RawUps = ups.Length, InjectedEvents = events.Count(e => e.Injected), Events = events }, Json));
        }
        finally
        {
            if (installed)
            {
                var current = await memory.ReadAsync(); var restore = MemoryWritePlan.Restore(current, before, true);
                if (restore.Changes.Count != 0) await memory.CommitAsync(restore, Path.Combine(folder, "backups"));
                if (before.Mode == 1) await memory.ActivateProfileAsync(before.ActiveSector); else await memory.SetModeAsync(false);
                var restored = await memory.ReadAsync(); await OnboardMemory.SaveBackupAsync(restored, Path.Combine(folder, "restored.json"));
                var identical = before.Sectors.All(p => p.Value.SequenceEqual(restored.Sectors[p.Key]));
                await File.WriteAllTextAsync(Path.Combine(folder, "restoration.json"), JsonSerializer.Serialize(new { AllSectorsIdentical = identical, ModeIdentical = restored.Mode == before.Mode, ActiveSectorIdentical = restored.ActiveSector == before.ActiveSector }, Json));
                if (!identical || restored.Mode != before.Mode || restored.ActiveSector != before.ActiveSector) throw new IOException("实测后的原始配置恢复核对未通过。");
            }
        }
    }
}
