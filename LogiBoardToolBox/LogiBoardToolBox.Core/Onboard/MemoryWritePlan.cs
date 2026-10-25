namespace LogiBoardToolBox.Core.Onboard;

public sealed record SectorChange(int Sector, byte[] Before, byte[] After, string Purpose);
public sealed record MemoryWritePlan(MemorySnapshot Baseline, IReadOnlyList<SectorChange> Changes,
    ushort ActivateSector, string Summary)
{
    internal bool RestoresErasedSectors { get; init; }
    public static MemoryWritePlan Create(MemorySnapshot snapshot, MouseProfile draft, IReadOnlyList<MacroAssignment> macros, bool allowExperimental = false)
    {
        var caps = snapshot.Capabilities;
        if (!caps.CanEditProfiles) throw new NotSupportedException("未知板载格式，仅允许备份。");
        if (caps.IsExperimental && !allowExperimental) throw new NotSupportedException("这只鼠标使用尚未完成实机写入验证的格式 10。请先阅读设备页说明，再决定是否启用实验性按键/宏写入。");
        if (snapshot.ReadErrors.Keys.Any(i => i < caps.SectorCount))
            throw new InvalidOperationException("部分可写扇区未能完整备份，请重新读取设备后再写入。");
        var profiles = snapshot.GetProfiles();
        var working = draft.Copy();
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
        var directory = MemorySnapshot.ParseDirectory(snapshot.Sectors[0], caps.ProfileCount);
        if (directory.Any(e => e.Sector > caps.ProfileCount))
            throw new NotSupportedException("检测到非常规配置目录，当前版本仅允许备份此布局。");
        if (!working.Enabled && profiles.All(p => p.Sector == working.Sector || !p.Enabled))
            throw new InvalidOperationException("至少需要保留一个启用的板载配置。");

        var changes = new List<SectorChange>();
        void Add(int sector, byte[] after, string purpose)
        {
            if (!snapshot.Sectors.TryGetValue(sector, out var before)) throw new InvalidDataException($"缺少扇区 {sector} 的原始备份。");
            if (sector < 0 || sector >= caps.SectorCount || sector >= 0x100 || after.Length != caps.SectorSize || !SectorChecksum.IsValid(after))
                throw new InvalidDataException("写入计划包含无效扇区。");
            if (!before.SequenceEqual(after)) changes.Add(new(sector, (byte[])before.Clone(), (byte[])after.Clone(), purpose));
        }

        var allocation = MacroAllocator.Allocate(snapshot, macros);
        foreach (var entry in allocation.Sectors) Add(entry.Key, entry.Value, "板载宏");
        foreach (var (binding, location) in allocation.Bindings)
            working.SetBinding(binding.Button, binding.GShift, ButtonBinding.Macro(location.Sector, location.Offset));
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
        return new(snapshot, changes, activate,
            $"目标：{snapshot.DeviceName}\n配置：{working.DisplayName}\n修改 {changes.Count} 个扇区，{changes.Count * caps.SectorSize} 字节；绑定 {macros.Count} 个宏。\n写入前自动备份；每个扇区写入后读回校验。完成后启用板载模式，关闭软件仍可使用。" +
            (macros.Any(m => m.Macro.Repeat != Macros.MacroRepeat.Once) ? "\n包含固件循环指令：此型号的循环触发行为仍需实测。" : ""));
    }

    public static MemoryWritePlan Restore(MemorySnapshot baseline, MemorySnapshot backup, bool allowExperimental = false)
    {
        var caps = baseline.Capabilities;
        if (caps.IsExperimental && !allowExperimental) throw new NotSupportedException("格式 10 的恢复也需要启用实验性写入。");
        if (!MemorySnapshot.IsKnownFormat(backup.Format) || backup.DeviceIdentity != baseline.DeviceIdentity ||
            !backup.Description.Take(11).SequenceEqual(baseline.Description.Take(11)))
            throw new InvalidDataException("备份不属于当前鼠标，或板载布局与当前固件不一致。");
        if (!caps.CanEditProfiles || baseline.ReadErrors.Keys.Any(s => s < caps.SectorCount))
            throw new InvalidDataException("当前设备未能完整备份，不能恢复。");
        if (!backup.Sectors.TryGetValue(0, out var dir) || dir.Length != caps.SectorSize ||
            MemorySnapshot.ParseDirectory(dir, caps.ProfileCount).Count == 0)
            throw new InvalidDataException("备份没有有效的用户配置目录，不能作为完整配置恢复。");
        if (backup.Sectors.Any(p => p.Key < 0 || p.Value is null || p.Value.Length != caps.SectorSize))
            throw new InvalidDataException("备份包含无效的扇区数据。");
        _ = MacroAllocator.FindReferencedSectors(backup);
        var changes = backup.Sectors.Where(p => p.Key < caps.SectorCount && (SectorChecksum.IsValid(p.Value) || p.Key > caps.ProfileCount && p.Value.All(b => b == 0xFF)))
            .OrderBy(p => p.Value.All(b => b == 0xFF) ? 3 : p.Key == 0 ? 2 : p.Key <= caps.ProfileCount ? 1 : 0).ThenBy(p => p.Key)
            .Where(p => !baseline.Sectors[p.Key].SequenceEqual(p.Value))
            .Select(p => new SectorChange(p.Key, (byte[])baseline.Sectors[p.Key].Clone(), (byte[])p.Value.Clone(), "恢复备份")).ToArray();
        var profiles = backup.GetProfiles();
        var active = profiles.FirstOrDefault(p => p.Sector == backup.ActiveSector && p.Enabled) ?? profiles.FirstOrDefault(p => p.Enabled)
            ?? throw new InvalidDataException("备份没有启用的配置。");
        return new(baseline, changes, active.Sector, $"恢复 {backup.CreatedAt:yyyy-MM-dd HH:mm:ss} 的备份\n目标：{backup.DeviceName}\n恢复 {changes.Length} 个扇区（包括原始空白宏空间）。恢复前会自动备份当前配置。") { RestoresErasedSectors = true };
    }
}
