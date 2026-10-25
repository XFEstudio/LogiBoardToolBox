using System.Text.Json;
using LogiBoardToolBox.Core.HidPlusPlus;

namespace LogiBoardToolBox.Core.Onboard;

public sealed record WriteResult(int SectorsWritten, string BackupPath);

public sealed class OnboardMemory(HidPlusPlusClient client, string deviceIdentity, string deviceName)
{
    private const ushort Feature = 0x8100;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<(byte Mode, ushort ActiveSector)> ActivateProfileAsync(ushort sector, CancellationToken ct = default)
    {
        await operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await ReadCoreAsync(null, ct).ConfigureAwait(false);
            if (!state.GetProfiles().Any(p => p.Sector == sector && p.Enabled)) throw new InvalidOperationException("只能选择已启用的配置槽。");
            await OnboardModeTransition.StopButtonSpyAsync(client, ct).ConfigureAwait(false);
            if (state.Mode != 1) await client.FeatureAsync(Feature, 1, [1], ct).ConfigureAwait(false);
            if (state.ActiveSector != sector || state.Mode != 1) await client.FeatureAsync(Feature, 3, [(byte)(sector >> 8), (byte)sector], ct).ConfigureAwait(false);
            var mode = (await client.FeatureAsync(Feature, 2, ct: ct).ConfigureAwait(false))[0];
            var active = await client.FeatureAsync(Feature, 4, ct: ct).ConfigureAwait(false);
            var actual = (ushort)((active[0] << 8) | active[1]);
            if (mode != 1 || actual != sector) throw new IOException("板载配置切换读回不一致。");
            return (mode, actual);
        }
        finally { operationGate.Release(); }
    }

    public async Task<(byte Mode, ushort ActiveSector)> SetModeAsync(bool onboard, CancellationToken ct = default)
    {
        await operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var expected = (byte)(onboard ? 1 : 2);
            if (onboard) await OnboardModeTransition.StopButtonSpyAsync(client, ct).ConfigureAwait(false);
            await client.FeatureAsync(Feature, 1, [expected], ct).ConfigureAwait(false);
            var mode = await client.FeatureAsync(Feature, 2, ct: ct).ConfigureAwait(false);
            var active = await client.FeatureAsync(Feature, 4, ct: ct).ConfigureAwait(false);
            if (mode[0] != expected) throw new IOException("设备模式未能保持，请检查是否有其他程序接管鼠标。");
            return (mode[0], (ushort)((active[0] << 8) | active[1]));
        }
        finally { operationGate.Release(); }
    }

    public async Task<MemorySnapshot> ReadAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await operationGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await ReadCoreAsync(progress, ct).ConfigureAwait(false); }
        finally { operationGate.Release(); }
    }

    private async Task<MemorySnapshot> ReadCoreAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var description = await client.FeatureAsync(Feature, 0, ct: ct).ConfigureAwait(false);
        var caps = OnboardCapabilities.Parse(description);
        if (caps.SectorSize is < 16 or > 4096 || caps.SectorCount == 0 || caps.FactoryProfileCount > 32)
            throw new NotSupportedException("板载容量回复异常，停止读取。");
        var mode = await client.FeatureAsync(Feature, 2, ct: ct).ConfigureAwait(false);
        var active = await client.FeatureAsync(Feature, 4, ct: ct).ConfigureAwait(false);
        var snapshot = new MemorySnapshot
        {
            DeviceIdentity = deviceIdentity, DeviceName = deviceName, Description = description,
            Mode = mode[0], ActiveSector = (ushort)((active[0] << 8) | active[1])
        };
        var sectors = Enumerable.Range(0, caps.SectorCount).Concat(Enumerable.Range(0x100, caps.FactoryProfileCount + 1)).ToArray();
        foreach (var sector in sectors)
        {
            progress?.Report($"读取存储扇区 0x{sector:X4}");
            try { snapshot.Sectors[sector] = await ReadSectorAsync(sector, caps.SectorSize, ct).ConfigureAwait(false); }
            catch (HidPlusPlusException ex) { snapshot.ReadErrors[sector] = ex.Message; }
        }
        return snapshot;
    }

    private async Task<byte[]> ReadSectorAsync(int sector, int size, CancellationToken ct)
    {
        var bytes = new byte[size];
        for (var position = 0; position < size; position += 16)
        {
            var offset = Math.Min(position, size - 16);
            var reply = await client.FeatureAsync(Feature, 5, [(byte)(sector >> 8), (byte)sector, (byte)(offset >> 8), (byte)offset], ct).ConfigureAwait(false);
            if (reply.Length < 16) throw new IOException("板载读取没有返回 16 字节的长回复。");
            reply.AsSpan(0, 16).CopyTo(bytes.AsSpan(offset));
        }
        return bytes;
    }

    private async Task WriteSectorAsync(int sector, byte[] data, CancellationToken ct)
    {
        await client.FeatureAsync(Feature, 6, [(byte)(sector >> 8), (byte)sector, 0, 0, (byte)(data.Length >> 8), (byte)data.Length], ct).ConfigureAwait(false);
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            var chunk = new byte[16];
            data.AsSpan(offset, Math.Min(16, data.Length - offset)).CopyTo(chunk);
            await client.FeatureAsync(Feature, 7, chunk, ct).ConfigureAwait(false);
        }
        try { await client.FeatureAsync(Feature, 8, ct: ct).ConfigureAwait(false); }
        catch (HidPlusPlusException ex) when (ex.Code == 4)
        {
            // Some firmware returns HARDWARE_ERROR on write-end; exact readback remains mandatory.
        }
        var actual = await ReadSectorAsync(sector, data.Length, ct).ConfigureAwait(false);
        if (!actual.SequenceEqual(data)) throw new IOException($"扇区 {sector} 读回校验失败。");
    }

    public static async Task SaveBackupAsync(MemorySnapshot snapshot, string path, CancellationToken ct = default)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshot, JsonOptions), ct).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<MemorySnapshot> LoadBackupAsync(string path, CancellationToken ct = default)
    {
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("备份文件超过大小限制。");
        var backup = JsonSerializer.Deserialize<MemorySnapshot>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false))
            ?? throw new InvalidDataException("备份为空。");
        if (!MemorySnapshot.IsKnownFormat(backup.Format) || backup.Description is null || backup.Description.Length < 11 ||
            backup.Sectors is null || backup.ReadErrors is null)
            throw new InvalidDataException("备份文件格式无效。");
        return backup;
    }

    public async Task<WriteResult> CommitAsync(MemoryWritePlan plan, string backupDirectory,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (plan.Baseline.DeviceIdentity != deviceIdentity) throw new InvalidOperationException("设备已更换，请重新读取。");
            var baseline = plan.Baseline;
            var caps = baseline.Capabilities;
            if (!caps.CanEditProfiles || plan.Changes.Select(c => c.Sector).Distinct().Count() != plan.Changes.Count ||
                plan.Changes.Any(c => c.Sector < 0 || c.Sector >= caps.SectorCount || c.After.Length != caps.SectorSize ||
                    c.Before.Length != caps.SectorSize || !(SectorChecksum.IsValid(c.After) || plan.RestoresErasedSectors && c.Sector > caps.ProfileCount && c.After.All(b => b == 0xFF)) ||
                    !baseline.Sectors.TryGetValue(c.Sector, out var b) || !b.SequenceEqual(c.Before)))
                throw new InvalidDataException("写入计划无效。");
            if (plan.RestoresErasedSectors)
            {
                var after = new MemorySnapshot { Description = baseline.Description, Sectors = baseline.Sectors.ToDictionary(p => p.Key, p => p.Value) };
                foreach (var change in plan.Changes) after.Sectors[change.Sector] = change.After;
                _ = MacroAllocator.FindReferencedSectors(after);
            }
            var currentDescription = await client.FeatureAsync(Feature, 0, ct: ct).ConfigureAwait(false);
            if (!currentDescription.Take(11).SequenceEqual(baseline.Description.Take(11))) throw new InvalidOperationException("设备固件或板载布局发生变化，请重新读取。");
            progress?.Report("检查设备是否被其他程序修改…");
            for (var sector = 0; sector < caps.SectorCount; sector++)
            {
                if (!baseline.Sectors.TryGetValue(sector, out var expected) ||
                    !(await ReadSectorAsync(sector, caps.SectorSize, ct).ConfigureAwait(false)).SequenceEqual(expected))
                    throw new InvalidOperationException("读取后鼠标配置已发生变化，请重新读取后再写入。");
            }
            var backupPath = Path.Combine(backupDirectory, $"onboard-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            await SaveBackupAsync(baseline, backupPath, ct).ConfigureAwait(false);
            progress?.Report("原始配置已备份，切换到板载模式…");
            var oldMode = (await client.FeatureAsync(Feature, 2, ct: ct).ConfigureAwait(false))[0];
            var oldActive = await client.FeatureAsync(Feature, 4, ct: ct).ConfigureAwait(false);
            var touched = new List<SectorChange>();
            try
            {
                await OnboardModeTransition.StopButtonSpyAsync(client, ct).ConfigureAwait(false);
                await client.FeatureAsync(Feature, 1, [1], ct).ConfigureAwait(false);
                foreach (var change in plan.Changes)
                {
                    // Include a partially written sector in rollback too.
                    touched.Add(change);
                    progress?.Report($"写入并校验 {change.Purpose} · 扇区 {change.Sector}");
                    await WriteSectorAsync(change.Sector, change.After, ct).ConfigureAwait(false);
                }
                await client.FeatureAsync(Feature, 3, [(byte)(plan.ActivateSector >> 8), (byte)plan.ActivateSector], ct).ConfigureAwait(false);
                var mode = await client.FeatureAsync(Feature, 2, ct: ct).ConfigureAwait(false);
                var active = await client.FeatureAsync(Feature, 4, ct: ct).ConfigureAwait(false);
                if (mode[0] != 1 || ((active[0] << 8) | active[1]) != plan.ActivateSector)
                    throw new IOException("写入完成，但无法确认板载模式和活动配置。");
                return new(touched.Count, backupPath);
            }
            catch (Exception writeError)
            {
                progress?.Report("写入未完成，正在尝试恢复原始数据…");
                var rollbackErrors = new List<string>();
                using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                foreach (var change in touched.AsEnumerable().Reverse())
                {
                    try { await WriteSectorAsync(change.Sector, change.Before, recovery.Token).ConfigureAwait(false); }
                    catch (Exception ex) { rollbackErrors.Add($"扇区 {change.Sector}: {ex.Message}"); }
                }
                try
                {
                    if (oldActive[0] != 0 || oldActive[1] != 0)
                        await client.FeatureAsync(Feature, 3, oldActive.Take(2).ToArray(), recovery.Token).ConfigureAwait(false);
                    await client.FeatureAsync(Feature, 1, [oldMode], recovery.Token).ConfigureAwait(false);
                }
                catch (Exception ex) { rollbackErrors.Add("模式恢复: " + ex.Message); }
                throw new IOException($"{writeError.Message}\n" +
                    (rollbackErrors.Count == 0 ? "已读回验证并恢复写入前的数据。" : "自动恢复未完成：" + string.Join("；", rollbackErrors)) +
                    $"\n原始备份：{backupPath}", writeError);
            }
        }
        finally { operationGate.Release(); }
    }
}
