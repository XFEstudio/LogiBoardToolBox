using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class BoardSaveRecoveryTests
{
    [Test]
    public async Task DuplicatePreviousReadReplyIsRetriedBeforeWriteAndDuringReadback()
    {
        var original = Fixture.Create(4);
        using var mouse = new FakeMouse(original);
        using var client = new HidPlusPlusClient(mouse, 0xFF);
        var memory = new OnboardMemory(client, original.DeviceIdentity, original.DeviceName);
        var baseline = await memory.ReadAsync();
        var profile = baseline.GetProfiles()[0].Copy();
        profile.Dpis = [850, 1200, 1600, 2400, 3200];
        var plan = MemoryWritePlan.Create(baseline, profile, []);
        using var backups = new ScratchDirectory();

        var dropped = new HashSet<int>();
        client.Timeout = TimeSpan.FromMilliseconds(125);
        mouse.RepeatPreviousReadReply = (sector, offset) => sector == 1 && offset == 32 && dropped.Add(mouse.WriteStarts);
        var result = await memory.CommitAsync(plan, backups.Path);
        Assert.Equal(true, dropped.SetEquals([0, 1]));
        Assert.Equal(1, mouse.WriteStarts);
        Assert.Equal(true, mouse.Sectors[1].SequenceEqual(plan.Changes.Single().After));
        var saved = await OnboardMemory.LoadBackupAsync(result.BackupPath);
        Assert.Equal(true, saved.Sectors[1].SequenceEqual(baseline.Sectors[1]));
    }

    [Test]
    public async Task G305RejectedTrailingAddressesPermitSavingAndRestoringRealMacroSector()
    {
        var original = G305();
        using var mouse = new FakeMouse(original);
        using var client = new HidPlusPlusClient(mouse, 1);
        var memory = new OnboardMemory(client, original.DeviceIdentity, original.DeviceName);
        using var backups = new ScratchDirectory();
        var baseline = await memory.ReadAsync();
        Assert.Equal(true, baseline.RejectedSectorAddresses.SetEquals([3, 4, 5]));
        Assert.Equal(3, baseline.WritableSectorCount);
        var macro = MacroCompiler.Compile("key A down\ndelay 13\nkey A up");
        var plan = MemoryWritePlan.Create(baseline, baseline.GetProfiles()[0], [new(4, false, macro)]);
        Assert.Equal(true, plan.Changes.Select(c => c.Sector).SequenceEqual([2, 1]));
        var save = await memory.CommitAsync(plan, backups.Path);
        var stored = await memory.ReadAsync();
        var binding = stored.GetProfiles()[0].GetBinding(4);
        var address = new MacroLocation(ButtonBinding.MacroSector(binding), ButtonBinding.MacroOffset(binding));
        Assert.Equal(true, MacroAllocator.ReadMacroBytes(stored, address).SequenceEqual(macro.Bytes));
        var backup = await OnboardMemory.LoadBackupAsync(save.BackupPath);
        await memory.CommitAsync(MemoryWritePlan.Restore(stored, backup), backups.Path);
        Assert.Equal(true, baseline.Sectors.All(entry => entry.Value.SequenceEqual(mouse.Sectors[entry.Key])));
    }

    [Test]
    public async Task G305MisleadingPartialReadOrChangedBoundaryIsRejectedBeforeWrite()
    {
        var original = G305();
        using var mouse = new FakeMouse(original);
        using var client = new HidPlusPlusClient(mouse, 1);
        var memory = new OnboardMemory(client, original.DeviceIdentity, original.DeviceName);
        using var backups = new ScratchDirectory();
        var baseline = await memory.ReadAsync();
        var draft = baseline.GetProfiles()[0].Copy();
        draft.Name = "Updated";
        var plan = MemoryWritePlan.Create(baseline, draft, []);
        mouse.Sectors[3] = new byte[16];
        var blocked = false;
        try { await memory.CommitAsync(plan, backups.Path); }
        catch (InvalidOperationException error) when (error.Message.Contains("存储范围已变化")) { blocked = true; }
        Assert.Equal(true, blocked);
        Assert.Equal(0, mouse.WriteStarts);

        var partial = await memory.ReadAsync();
        Assert.Equal(true, partial.RejectedSectorAddresses.SetEquals([4, 5]));
        Assert.Equal(6, partial.WritableSectorCount);
        blocked = false;
        try { _ = MemoryWritePlan.Create(partial, draft, []); }
        catch (InvalidOperationException error) when (error.Message.Contains("未能完整备份")) { blocked = true; }
        Assert.Equal(true, blocked);
        Assert.Equal(0, mouse.WriteStarts);
    }

    private static MemorySnapshot G305()
    {
        var snapshot = Fixture.Create(3);
        snapshot.Description = Convert.FromHexString("0103010101060600FF0A020000000000");
        foreach (var sector in snapshot.Sectors.Keys.Where(s => s is > 2 and < 256 or > 257).ToArray())
            snapshot.Sectors.Remove(sector);
        snapshot.Sectors[2].AsSpan().Fill(0xFF);
        snapshot.Sectors[0].AsSpan(4).Fill(0xFF);
        SectorChecksum.Seal(snapshot.Sectors[0]);
        return snapshot;
    }
}
