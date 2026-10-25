using System.Text.Json;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Core.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class SnapshotRestore
{
    public static async Task RunAsync(LogitechDevice device, string source, string folder)
    {
        Directory.CreateDirectory(folder);
        var desired = await OnboardMemory.LoadBackupAsync(source);
        using var client = LogitechDiscovery.Open(device);
        var memory = new OnboardMemory(client, device.Identity, device.Name);
        var current = await memory.ReadAsync();
        var plan = MemoryWritePlan.Restore(current, desired, true);
        await OnboardMemory.SaveBackupAsync(current, Path.Combine(folder, "before-restore.json"));
        if (plan.Changes.Count != 0) await memory.CommitAsync(plan, Path.Combine(folder, "backups"), new Progress<string>(Console.WriteLine));
        if (desired.Mode == 1) await memory.ActivateProfileAsync(desired.ActiveSector); else await memory.SetModeAsync(false);
        var after = await memory.ReadAsync();
        await OnboardMemory.SaveBackupAsync(after, Path.Combine(folder, "after-restore.json"));
        var matches = desired.Sectors.All(p => after.Sectors.TryGetValue(p.Key, out var value) && p.Value.SequenceEqual(value));
        var state = desired.Mode == after.Mode && desired.ActiveSector == after.ActiveSector;
        await File.WriteAllTextAsync(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { device.Identity, AllSectorsMatch = matches, ModeAndActiveMatch = state, SectorsWritten = plan.Changes.Count }, new JsonSerializerOptions { WriteIndented = true }));
        if (!matches || !state) throw new IOException("恢复后的全量核对失败。");
        Console.WriteLine($"完整恢复已核对：{after.Sectors.Count} 个扇区，模式 {after.Mode}，配置 {after.ActiveSector}。");
    }
}
