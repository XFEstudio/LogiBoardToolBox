using LogiBoardToolBox.Core.Diagnostics;
using LogiBoardToolBox.Core.Lighting;

namespace LogiBoardToolBox.Core.Onboard;

public sealed record SectorChange(int Sector, byte[] Before, byte[] After, string Purpose);
public sealed record MemoryWritePlan(MemorySnapshot Baseline, IReadOnlyList<SectorChange> Changes,
    ushort ActivateSector, string Summary)
{
    internal bool RestoresErasedSectors { get; init; }

    /// <summary>Repairs only CRC bytes proven by an identity-matched valid backup.
    /// Every payload byte must still match; changed or missing macro data is never guessed.</summary>
    public static MemoryWritePlan CreateRecoveringMacroCrc(MemorySnapshot snapshot, MouseProfile draft,
        IReadOnlyList<MacroAssignment> macros, IEnumerable<MemorySnapshot> backups, bool allowExperimental = false,
        (LightsyncCapabilities Device, LightingSettings Preset)? activeRgb = null)
    {
        var caps = snapshot.Capabilities;
        var verified = backups.Where(b => MemorySnapshot.IsKnownFormat(b.Format) && b.DeviceIdentity == snapshot.DeviceIdentity
            && b.Description is not null && b.Description.Length >= 11 && b.Description.Take(11).SequenceEqual(snapshot.Description.Take(11))).ToArray();
        var repaired = new MemorySnapshot { Format = snapshot.Format, CreatedAt = snapshot.CreatedAt,
            DeviceIdentity = snapshot.DeviceIdentity, DeviceName = snapshot.DeviceName, Description = (byte[])snapshot.Description.Clone(),
            Mode = snapshot.Mode, ActiveSector = snapshot.ActiveSector,
            ReadErrors = new(snapshot.ReadErrors), RejectedSectorAddresses = new(snapshot.RejectedSectorAddresses),
            Sectors = snapshot.Sectors.ToDictionary(p => p.Key, p => (byte[])p.Value.Clone()) };
        var repairs = new List<SectorChange>();
        while (true)
        {
            try
            {
                var plan = Create(repaired, draft, macros, allowExperimental, activeRgb);
                if (repairs.Count == 0) return plan;
                var summary = $"已从内容完全匹配的有效备份恢复 {repairs.Count} 个宏扇区的 CRC；宏动作与延迟未改变。\n" + BuildSummary(snapshot, draft, macros, repairs.Count + plan.Changes.Count);
                var result = new MemoryWritePlan(snapshot, repairs.Concat(plan.Changes).ToArray(), plan.ActivateSector, summary);
                DiagnosticLog.Record("memory-recovery", "verified recovery plan", result);
                return result;
            }
            catch (MacroStorageException error)
            {
                var sector = error.Sector;
                if (sector <= caps.ProfileCount || sector >= caps.SectorCount || !repaired.Sectors.TryGetValue(sector, out var current)
                    || current.Length != caps.SectorSize || SectorChecksum.IsValid(current) || SectorChecksum.IsErased(current)) throw;
                var proof = verified.FirstOrDefault(b => b.Sectors is not null && b.Sectors.TryGetValue(sector, out var candidate)
                    && candidate is not null && candidate.Length == current.Length && SectorChecksum.IsValid(candidate)
                    && candidate.AsSpan(0, candidate.Length - 2).SequenceEqual(current.AsSpan(0, current.Length - 2)));
                if (proof is null)
                {
                    DiagnosticLog.Record("memory-recovery", "no exactly matching valid backup", new { sector }, error);
                    throw;
                }
                var restored = (byte[])proof.Sectors[sector].Clone();
                repairs.Add(new(sector, (byte[])snapshot.Sectors[sector].Clone(), restored, "从匹配备份恢复宏校验"));
                repaired.Sectors[sector] = restored;
                DiagnosticLog.Record("memory-recovery", "CRC matched to backup", new { sector, proof.CreatedAt, proof.DeviceIdentity,
                    Before = Convert.ToHexString(current), After = Convert.ToHexString(restored) });
            }
        }
    }

    public static MemoryWritePlan Create(MemorySnapshot snapshot, MouseProfile draft, IReadOnlyList<MacroAssignment> macros,
        bool allowExperimental = false, (LightsyncCapabilities Device, LightingSettings Preset)? activeRgb = null)
    {
        var caps = snapshot.Capabilities;
        if (!caps.CanEditProfiles) throw new NotSupportedException("未知板载格式，仅允许备份。");
        if (caps.IsExperimental && !allowExperimental) throw new NotSupportedException("这只鼠标使用尚未完成实机写入验证的格式 10。请先阅读设备页说明，再决定是否启用实验性按键/宏写入。");
        if (snapshot.HasWritableReadErrors)
            throw new InvalidOperationException("部分可写扇区未能完整备份，请重新读取设备后再写入。");
        var profiles = snapshot.GetProfiles();
        var working = draft.Copy();
        if (caps.UsesAdvancedProfile)
        {
            var originalProfile = profiles.Single(p => p.Sector == working.Sector);
            var original = originalProfile.ToBytes();
            var candidate = working.ToBytes();
            var newStageLod = originalProfile.GetNewDpiStageLod();
            for (var offset = 0; offset < candidate.Length - 2; offset++)
            {
                var known = offset is < 29 or >= 44 and < 96 or >= 112 and < 208;
                if (!known && candidate[offset] != original[offset])
                    throw new InvalidOperationException("格式 7 的未知字段必须原样保留。");
                if (offset is < 8 or > 28 || (offset - 8) % 5 != 0) continue;
                var stage = (offset - 8) / 5;
                var oldX = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(4 + stage * 5));
                var newX = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(candidate.AsSpan(4 + stage * 5));
                var expectedLod = original[offset];
                if (newX == 0) expectedLod = 0;
                else if (oldX == 0 && original[offset] == 0) expectedLod = newStageLod;
                if (candidate[offset] != expectedLod)
                    throw new InvalidOperationException("修改配置时必须保留格式 7 原有的抬升距离。");
            }
        }
        if (caps.IsExperimental)
        {
            var original = profiles.Single(p => p.Sector == working.Sector).ToBytes();
            var candidate = working.ToBytes();
            // Preserve every unrecognized extension byte, including 0x54/0x55 and 0xFC.
            for (var offset = 0; offset < candidate.Length - 2; offset++)
            {
                var known = offset is >= 1 and < 13 or >= 20 and < 22 or >= 28 and < 84 or >= 86 and < 148 or >= 160 and < 252;
                if (!known && candidate[offset] != original[offset])
                    throw new InvalidOperationException("格式 10 的未知字段必须原样保留。");
            }
        }
        // Apply the typed RGB edit after checking that the draft retained all other format-7 bytes.
        if (activeRgb is { } lighting)
            OnboardLighting.ApplyActiveRgbPreset(working, caps, lighting.Device, lighting.Preset);
        var directory = MemorySnapshot.ParseDirectory(snapshot.Sectors[0], caps.ProfileCount);
        if (directory.Any(e => e.Sector > caps.ProfileCount))
            throw new NotSupportedException("检测到非常规配置目录，当前版本仅允许备份此布局。");
        if (!working.Enabled && profiles.All(p => p.Sector == working.Sector || !p.Enabled))
            throw new InvalidOperationException("至少需要保留一个启用的板载配置。");

        var changes = new List<SectorChange>();
        void Add(int sector, byte[] after, string purpose)
        {
            if (!snapshot.Sectors.TryGetValue(sector, out var before)) throw new InvalidDataException($"缺少扇区 {sector} 的原始备份。");
            if (sector < 0 || sector >= snapshot.WritableSectorCount || sector >= 0x100 || !caps.IsValidSectorWrite(sector, after))
                throw new InvalidDataException("写入计划包含无效扇区。");
            if (!before.SequenceEqual(after)) changes.Add(new(sector, (byte[])before.Clone(), (byte[])after.Clone(), purpose));
        }

        var allocation = MacroAllocator.Allocate(snapshot, macros, working.Sector);
        foreach (var entry in allocation.Sectors) Add(entry.Key, entry.Value, "板载宏");
        foreach (var assignment in macros)
        {
            var location = allocation.Bindings[(assignment.ButtonIndex, assignment.GShift)];
            working.SetBinding(assignment.ButtonIndex, assignment.GShift, assignment.Macro.Repeat == Macros.MacroRepeat.Toggle
                ? ButtonBinding.NativeToggle(caps, location.Sector, location.Offset) : ButtonBinding.Macro(location.Sector, location.Offset));
        }
        working.Validate(caps);
        foreach (var profile in profiles.Where(p => p.FromFactory && p.Sector != working.Sector)) Add(profile.Sector, profile.ToBytes(), "初始化出厂配置");
        Add(working.Sector, working.ToBytes(), "配置、按键与灯效");

        // Directory is always last: it must never point at data which has not been written yet.
        var dir = directory.Count != 0 ? (byte[])snapshot.Sectors[0].Clone() : Enumerable.Repeat((byte)0xFF, caps.SectorSize).ToArray();
        for (var i = 0; i < caps.ProfileCount; i++)
        {
            var slot = i + 1;
            dir[4 * i] = 0;
            dir[4 * i + 1] = (byte)slot;
            dir[4 * i + 2] = (byte)((slot == working.Sector ? working.Enabled : profiles[i].Enabled) ? 1 : 0);
            if (directory.Count == 0) dir[4 * i + 3] = 0;
        }
        dir[4 * caps.ProfileCount] = 0xFF;
        dir[4 * caps.ProfileCount + 1] = 0xFF;
        Add(0, SectorChecksum.Seal(dir), "配置目录");
        var activate = working.Enabled ? working.Sector : profiles.First(p => p.Enabled && p.Sector != working.Sector).Sector;
        return new(snapshot, changes, activate, BuildSummary(snapshot, working, macros, changes.Count));
    }

    private static string BuildSummary(MemorySnapshot snapshot, MouseProfile profile, IReadOnlyList<MacroAssignment> macros, int changes) =>
        $"目标：{snapshot.DeviceName}\n配置：{profile.DisplayName}\n修改 {changes} 个扇区，{changes * snapshot.Capabilities.SectorSize} 字节；绑定 {macros.Count} 个宏。\n写入前自动备份；每个扇区写入后读回校验。完成后启用板载模式，关闭软件仍可使用。" +
        (macros.Any(m => m.Macro.Repeat != Macros.MacroRepeat.Once) ? "\n包含固件循环指令：此型号的循环触发行为仍需实测。" : "");

    public static MemoryWritePlan Restore(MemorySnapshot baseline, MemorySnapshot backup, bool allowExperimental = false)
    {
        var caps = baseline.Capabilities;
        if (caps.IsExperimental && !allowExperimental) throw new NotSupportedException("格式 10 的恢复也需要启用实验性写入。");
        if (!MemorySnapshot.IsKnownFormat(backup.Format) || backup.DeviceIdentity != baseline.DeviceIdentity ||
            !backup.Description.Take(11).SequenceEqual(baseline.Description.Take(11)) || backup.Capabilities.SupportsNativeToggle && !caps.SupportsNativeToggle ||
            backup.Capabilities.SupportsNativeLua && !caps.SupportsNativeLua)
            throw new InvalidDataException("备份不属于当前鼠标，或板载布局与当前固件不一致。");
        if (!caps.CanEditProfiles || baseline.HasWritableReadErrors)
            throw new InvalidDataException("当前设备未能完整备份，不能恢复。");
        if (!backup.Sectors.TryGetValue(0, out var dir) || dir.Length != caps.SectorSize ||
            MemorySnapshot.ParseDirectory(dir, caps.ProfileCount).Count == 0)
            throw new InvalidDataException("备份没有有效的用户配置目录，不能作为完整配置恢复。");
        if (backup.Sectors.Any(p => p.Key < 0 || p.Value is null || p.Value.Length != caps.SectorSize))
            throw new InvalidDataException("备份包含无效的扇区数据。");
        if (backup.Sectors.Keys.Any(s => s < caps.SectorCount && !baseline.Sectors.ContainsKey(s)))
            throw new InvalidDataException("当前设备无法访问备份中的全部扇区，请重新读取设备后再恢复。");
        _ = MacroAllocator.FindReferencedSectors(backup);
        var changes = backup.Sectors.Where(p => p.Key < caps.SectorCount && caps.IsValidSectorWrite(p.Key, p.Value, allowErased: true))
            .OrderBy(p => p.Value.All(b => b == 0xFF) ? 3 : p.Key == 0 ? 2 : p.Key <= caps.ProfileCount ? 1 : 0).ThenBy(p => p.Key)
            .Where(p => !baseline.Sectors[p.Key].SequenceEqual(p.Value))
            .Select(p => new SectorChange(p.Key, (byte[])baseline.Sectors[p.Key].Clone(), (byte[])p.Value.Clone(), "恢复备份")).ToArray();
        var profiles = backup.GetProfiles();
        var active = profiles.FirstOrDefault(p => p.Sector == backup.ActiveSector && p.Enabled) ?? profiles.FirstOrDefault(p => p.Enabled)
            ?? throw new InvalidDataException("备份没有启用的配置。");
        return new(baseline, changes, active.Sector, $"恢复 {backup.CreatedAt:yyyy-MM-dd HH:mm:ss} 的备份\n目标：{backup.DeviceName}\n恢复 {changes.Length} 个扇区（包括原始空白宏空间）。恢复前会自动备份当前配置。") { RestoresErasedSectors = true };
    }
}
