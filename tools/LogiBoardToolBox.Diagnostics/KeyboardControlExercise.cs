using System.Text.Json;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Core.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class KeyboardControlExercise
{
    public static async Task RunAsync(LogitechDevice device, string folder, bool pureKeyboard = false)
    {
        Directory.CreateDirectory(folder);
        if (File.Exists(Path.Combine(folder, "start.signal"))) throw new IOException("需要新的测试目录。");
        using var client = LogitechDiscovery.Open(device);
        var memory = new OnboardMemory(client, device.Identity, device.Name); var before = await memory.ReadAsync();
        await OnboardMemory.SaveBackupAsync(before, Path.Combine(folder, "before.json"));
        var profile = before.GetProfiles().Single(p => p.Sector == before.ActiveSector && p.Enabled).Copy();
        if (!pureKeyboard) profile.SetBinding(4, false, ButtonBinding.Keyboard(0, 0x31));
        var marker = "mouse 1 down\ndelay 20\nmouse 1 up\ndelay 80";
        var keys = "key 0x31 down\ndelay 59\nkey 0x31 up\ndelay 198\nkey 0x1E down\ndelay 25\nkey 0x1E up\ndelay 10";
        var script = pureKeyboard ? keys : marker + "\n" + keys + "\n" + marker;
        var macro = MacroCompiler.Compile(script);
        var plan = MemoryWritePlan.Create(before, profile, [new(5, false, macro)], true);
        await Save("plan.json", new { PureKeyboard = pureKeyboard, DirectKey = new { ButtonIndex = 4, HidUsage = 0x31, TemporarilyAssigned = !pureKeyboard }, TestMacro = new { ButtonIndex = 5, Bytecode = Convert.ToHexString(macro.Bytes) }, RestoreAllSectorsAtEnd = true, Changes = plan.Changes.Select(c => new { c.Sector, c.Purpose, Before = Convert.ToHexString(c.Before), After = Convert.ToHexString(c.After) }) });
        using var probe = new PhysicalMacroProbe(device.Hid.ProductId); await probe.Ready;
        await Save("prepared.json", new { device.Identity, NoInputGenerated = true, NoFlashWritten = true });
        Console.WriteLine(pureKeyboard ? "纯键盘对照已准备：只临时将 G6 设为单次纯键盘取消刀，G5 原有按住宏保留。等待开始信号。" : "键盘对照已准备；G5 临时直接绑定反斜杠，G6 临时绑定带点击标记的单次取消刀。等待开始信号，结束全量恢复。");
        var deadline = DateTime.UtcNow.AddMinutes(15);
        while (!File.Exists(Path.Combine(folder, "start.signal")))
        {
            if (DateTime.UtcNow >= deadline) return;
            await Task.Delay(200);
        }
        var installed = false;
        try
        {
            await memory.CommitAsync(plan, Path.Combine(folder, "backups")); installed = true;
            var actual = await memory.ReadAsync(); await OnboardMemory.SaveBackupAsync(actual, Path.Combine(folder, "installed.json"));
            if (plan.Changes.Any(c => !actual.Sectors[c.Sector].SequenceEqual(c.After)) || actual.Mode != 1) throw new IOException("对照配置读回不一致。");
            var spyStopped = false;
            if (pureKeyboard && await client.GetFeatureAsync(0x8110) != 0)
            {
                await client.FeatureAsync(0x8110, 2); spyStopped = true;
            }
            probe.Arm(TimeSpan.FromSeconds(30));
            await Save("armed.json", new { ArmedAt = DateTimeOffset.Now, Seconds = 30, NoSoftwareMacroEngine = true, MouseButtonSpyStopped = spyStopped });
            Console.WriteLine(pureKeyboard ? "ARMED：先按键盘数字 1 一次校验探针，再单按 G6 一次，等 2 秒后按住 G5 一秒再松开。" : "ARMED：请按取消刀实体键一次，等 2 秒，再按左键连点实体键一次。");
            while (probe.Armed) await Task.Delay(100);
            var observations = probe.Events;
            await Save("observations.json", new { Events = observations, ReceiverKeyboardEvents = observations.Count(e => e.Source == "raw-input" && e.Kind == "keyboard"), ReceiverLeftClicks = observations.Count(e => e.Source == "raw-input" && e.Kind == "mouse" && e.Down), InjectedEvents = observations.Count(e => e.Injected) });
        }
        finally
        {
            if (installed)
            {
                var current = await memory.ReadAsync(); var restore = MemoryWritePlan.Restore(current, before, true);
                if (restore.Changes.Count != 0) await memory.CommitAsync(restore, Path.Combine(folder, "backups"));
                if (before.Mode == 1) await memory.ActivateProfileAsync(before.ActiveSector); else await memory.SetModeAsync(false);
                var after = await memory.ReadAsync(); await OnboardMemory.SaveBackupAsync(after, Path.Combine(folder, "restored.json"));
                var unchanged = before.Sectors.All(p => p.Value.SequenceEqual(after.Sectors[p.Key]));
                await Save("restoration.json", new { AllSectorsIdentical = unchanged, ModeIdentical = before.Mode == after.Mode, ActiveSectorIdentical = before.ActiveSector == after.ActiveSector });
                if (!unchanged || before.Mode != after.Mode || before.ActiveSector != after.ActiveSector) throw new IOException("键盘对照结束后的恢复校验未通过。");
            }
        }
        Console.WriteLine("键盘对照结束，已全量恢复修正后的用户配置。");
        Task Save(string name, object value) => File.WriteAllTextAsync(Path.Combine(folder, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
