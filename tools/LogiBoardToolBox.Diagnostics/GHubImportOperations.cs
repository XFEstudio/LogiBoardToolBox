using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Core.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class GHubImportOperations
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static async Task RunAsync(LogitechDevice device, string settingsPath, string folder, bool write)
    {
        var model = device.Model;
        if (!model.IsKnown) throw new NotSupportedException("此鼠标的 G HUB 槽位尚未核对。");
        Directory.CreateDirectory(folder);
        if (write && File.Exists(Path.Combine(folder, "completed.json"))) throw new IOException("此导入已完成，请先查看读回结果。");
        var sourceBytes = await File.ReadAllBytesAsync(settingsPath);
        var source = System.Text.Encoding.UTF8.GetString(sourceBytes);
        var import = GHubMacroImport.Read(source, model.SlotPrefix);
        using (var document = JsonDocument.Parse(source))
        {
            var known = document.RootElement.GetProperty("devices/known").GetProperty("knownList");
            var serial = uint.Parse(device.UnitId, NumberStyles.HexNumber, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            if (!known.EnumerateArray().Any(d => d.GetProperty("baseModelId").GetString() == model.GHubModelId
                && d.GetProperty("serialNumber").GetString() == serial)) throw new InvalidDataException("G HUB 配置记录的鼠标序列号与当前设备不一致。");
        }
        using var client = LogitechDiscovery.Open(device);
        var memory = new OnboardMemory(client, device.Identity, device.Name);
        var before = await memory.ReadAsync();
        await OnboardMemory.SaveBackupAsync(before, Path.Combine(folder, write ? "onboard-before-import.json" : "onboard-before-plan.json"));
        var profile = (before.GetProfiles().FirstOrDefault(p => p.Enabled && before.Mode == 1 && p.Sector == before.ActiveSector)
            ?? before.GetProfiles().First(p => p.Enabled)).Copy();
        var plan = MemoryWritePlan.Create(before, profile, import.Bindings.Select(b => new MacroAssignment(b.ButtonIndex, b.GShift, b.Compiled)).ToArray(), true);
        var report = new
        {
            SourceSha256 = Convert.ToHexString(SHA256.HashData(sourceBytes)), device.Identity,
            import.ProfileId, import.ProfileName, TargetSector = profile.Sector,
            Scope = "Assigned recorded macros and their button bindings; all other profile fields preserved",
            Timing = "Exact serialized durationMs components; no inserted uniform delay",
            Macros = import.Bindings.Select(b => new { b.SlotId, b.ButtonIndex, b.GShift, b.CardId, b.Name, Repeat = b.Repeat.ToString(), b.Script, b.HoldScript, b.ReleaseScript, b.Color, b.RecordedDelays, Bytecode = Convert.ToHexString(b.Compiled.Bytes) }),
            Changes = plan.Changes.Select(c => new { c.Sector, c.Purpose, Before = Convert.ToHexString(c.Before), After = Convert.ToHexString(c.After) })
        };
        await File.WriteAllTextAsync(Path.Combine(folder, write ? "import-plan.json" : "preview-plan.json"), JsonSerializer.Serialize(report, Json));
        foreach (var binding in import.Bindings)
            Console.WriteLine($"{binding.SlotId}: {binding.Name} · {binding.Repeat} · 录制延迟 [{string.Join(",", binding.RecordedDelays)}] ms · {Convert.ToHexString(binding.Compiled.Bytes)}");
        if (!write) { Console.WriteLine("仅生成计划，尚未写入鼠标。"); return; }
        var committed = false;
        try
        {
            var result = await memory.CommitAsync(plan, Path.Combine(folder, "backups"), new Progress<string>(Console.WriteLine)); committed = true;
            var after = await memory.ReadAsync();
            await OnboardMemory.SaveBackupAsync(after, Path.Combine(folder, "onboard-after-import.json"));
            var expected = before.Sectors.ToDictionary(p => p.Key, p => p.Value);
            foreach (var change in plan.Changes) expected[change.Sector] = change.After;
            var allSectorsMatch = after.ReadErrors.Count == 0 && expected.All(p => after.Sectors.TryGetValue(p.Key, out var bytes) && bytes.SequenceEqual(p.Value));
            var actualProfile = after.GetProfiles().Single(p => p.Sector == profile.Sector);
            var checks = import.Bindings.Select(b =>
            {
                var binding = actualProfile.GetBinding(b.ButtonIndex, b.GShift);
                var actual = ButtonBinding.IsMacro(binding) ? MacroAllocator.ReadMacroBytes(after, new(ButtonBinding.MacroSector(binding), ButtonBinding.MacroOffset(binding))) : [];
                return new { b.Name, b.SlotId, Binding = Convert.ToHexString(binding), MacroSector = ButtonBinding.MacroSector(binding), MacroOffset = ButtonBinding.MacroOffset(binding), Bytecode = Convert.ToHexString(actual), ExactMatch = actual.SequenceEqual(b.Compiled.Bytes) };
            }).ToArray();
            if (!allSectorsMatch || checks.Any(c => !c.ExactMatch) || after.Mode != 1 || after.ActiveSector != profile.Sector)
                throw new IOException("导入后全量读回验证未通过。");
            await File.WriteAllTextAsync(Path.Combine(folder, "macro-library.json"), JsonSerializer.Serialize(import.Bindings.DistinctBy(b => b.CardId).Select(b => new
            {
                b.Name, b.Script, Repeat = (int)b.Repeat, b.HoldScript, b.ReleaseScript, UseStandardDelay = false,
                StandardDelay = "50", ShowUpDown = true, b.Color
            }), Json));
            await File.WriteAllTextAsync(Path.Combine(folder, "completed.json"), JsonSerializer.Serialize(new
            {
                CompletedAt = DateTimeOffset.Now, device.Identity, result.BackupPath, result.SectorsWritten,
                after.Mode, after.ActiveSector, AllSectorsMatchExpected = allSectorsMatch, Checks = checks,
                RecordedDelayComponentsPreserved = true, SoftwareMacroEngine = false, PhysicalRepeatTested = false
            }, Json));
            Console.WriteLine($"导入完成：板载配置 {after.ActiveSector}，{checks.Length} 个宏，全部字节码及 {after.Sectors.Count} 个扇区读回一致。备份：{result.BackupPath}");
        }
        catch
        {
            if (committed)
            {
                var current = await memory.ReadAsync();
                var restore = MemoryWritePlan.Restore(current, before, true);
                if (restore.Changes.Count != 0) await memory.CommitAsync(restore, Path.Combine(folder, "recovery-backups"));
                if (before.Mode == 1) await memory.ActivateProfileAsync(before.ActiveSector); else await memory.SetModeAsync(false);
                var restored = await memory.ReadAsync();
                var identical = before.Sectors.All(p => p.Value.SequenceEqual(restored.Sectors[p.Key]));
                await File.WriteAllTextAsync(Path.Combine(folder, "failed-import-restoration.json"), JsonSerializer.Serialize(new { AllSectorsIdentical = identical, restored.Mode, restored.ActiveSector }, Json));
                if (!identical) throw new IOException("导入失败后的恢复校验未通过，请使用原始备份恢复。");
            }
            throw;
        }
    }
}
