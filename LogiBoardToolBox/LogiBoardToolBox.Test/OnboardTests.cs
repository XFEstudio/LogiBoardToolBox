using System.Text;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class OnboardTests
{
    [Test] public void CrcMatchesPublishedCheckVector() => Assert.Equal((ushort)0x29B1, SectorChecksum.Compute(Encoding.ASCII.GetBytes("123456789")));

    [Test]
    public void EditingBindingPreservesUnknownBytes()
    {
        var snapshot = Fixture.Create();
        var original = snapshot.Sectors[1];
        var profile = snapshot.GetProfiles()[0].Copy();
        profile.SetBinding(3, true, ButtonBinding.Keyboard(1, 6));
        var changed = profile.ToBytes();
        for (var i = 0; i < changed.Length - 2; i++)
            if (i is < 108 or >= 112) Assert.Equal(original[i], changed[i]);
        Assert.Equal(true, SectorChecksum.IsValid(changed));
    }

    [Test]
    public void EmptyNameDoesNotReplaceFactoryPadding()
    {
        var snapshot = Fixture.Create(); var profile = snapshot.GetProfiles()[0];
        profile.Name = profile.Name;
        Assert.Equal(Convert.ToHexString(snapshot.Sectors[1]), Convert.ToHexString(profile.ToBytes()));
    }

    [Test]
    public void MacroCompilesToFirmwareOpcodes()
    {
        var compiled = MacroCompiler.Compile("tap Ctrl+C\ndelay 50\ntap Ctrl+V");
        Assert.Equal("430106440106400032430119440119FF", Convert.ToHexString(compiled.Bytes));
        Assert.Equal("43000444000402FF", Convert.ToHexString(MacroCompiler.Compile("tap A", MacroRepeat.WhileHeld).Bytes));
    }

    [Test]
    public void InvalidMacrosAreRejectedBeforePlanning()
    {
        Reject<FormatException>(() => MacroCompiler.Compile("key Shift down"));
        Reject<FormatException>(() => MacroCompiler.Compile("key A up"));
        Reject<FormatException>(() => MacroCompiler.Compile("delay 65536"));
        Reject<FormatException>(() => MacroCompiler.Compile("tap 中文"));
        Reject<FormatException>(() => MacroCompiler.Compile("mouse 0 down"));
        Reject<FormatException>(() => MacroCompiler.Compile("tap A+B"));
    }

    [Test] public void MouseBindingsUseBitMasks() => Assert.Equal("80010010", Convert.ToHexString(ButtonBinding.Mouse(5)));

    [Test]
    public void StandardDelaySeparatesDownAndUpEvents()
    {
        var macro = MacroCompiler.WithStandardDelay(MacroCompiler.Compile("tap A"), 50);
        Assert.Equal("430004400032440004400032FF", Convert.ToHexString(macro.Bytes));
    }

    [Test]
    public async Task StandardDelaySeparatesSuccessiveOnboardClicks()
    {
        const string expected = "41000140003242000140003202FF";
        var program = MacroCompiler.CompileProgram("mouse 1 down\nmouse 1 up", MacroRepeat.WhileHeld, standardDelay: 50);
        var macro = program.ToOnboard();
        Assert.Equal(expected, Convert.ToHexString(macro.Bytes));

        var baseline = Fixture.Create(10);
        using var fake = new FakeMouse(baseline);
        using var client = new HidPlusPlusClient(fake, 1);
        var memory = new OnboardMemory(client, baseline.DeviceIdentity, baseline.DeviceName);
        var profile = baseline.GetProfiles()[0].Copy();
        var plan = MemoryWritePlan.Create(baseline, profile, [new(5, false, macro)], true);
        using var scratch = new ScratchDirectory();
        await memory.CommitAsync(plan, scratch.Path);
        var after = await memory.ReadAsync();
        var binding = after.GetProfiles()[0].GetBinding(5);
        var address = new MacroLocation(ButtonBinding.MacroSector(binding), ButtonBinding.MacroOffset(binding));
        Assert.Equal(expected, Convert.ToHexString(MacroAllocator.ReadMacroBytes(after, address)));
    }

    [Test]
    public void StandardDelayPreservesExplicitLoopGapsAndOneShotExit()
    {
        const string click = "mouse 1 down\nmouse 1 up";
        string Compile(string script, MacroRepeat repeat = MacroRepeat.WhileHeld, ushort delay = 50)
            => Convert.ToHexString(MacroCompiler.CompileProgram(script, repeat, standardDelay: delay).ToOnboard().Bytes);

        Assert.Equal("410001400032420001FF", Compile(click, MacroRepeat.Once));
        Assert.Equal("41000142000102FF", Compile(click, delay: 0));
        Assert.Equal("41000140000A42000140001402FF", Compile("mouse 1 down\ndelay 10\nmouse 1 up\ndelay 20"));
        Assert.Equal("40001441000140003242000102FF", Compile("delay 20\n" + click));
    }

    [Test]
    public void SequenceIsNotSilentlyConvertedToToggle()
        => Reject<NotSupportedException>(() => MacroCompiler.Compile("tap A", MacroRepeat.Sequence));

    [Test]
    public async Task SwitchingModeDoesNotWriteFlash()
    {
        using var fake = new FakeMouse(Fixture.Create()); using var client = new HidPlusPlusClient(fake, 1);
        var memory = new OnboardMemory(client, "test-device", "Test Mouse");
        var onboard = await memory.SetModeAsync(true); Assert.Equal((byte)1, onboard.Mode);
        var host = await memory.SetModeAsync(false); Assert.Equal((byte)2, host.Mode);
        Assert.Equal(0, fake.WriteStarts);
    }

    [Test]
    public void SharedMacrosArePackedAndDeduplicated()
    {
        var snapshot = Fixture.Create(); var macro = MacroCompiler.Compile("tap A");
        var allocation = MacroAllocator.Allocate(snapshot, [new(3, false, macro), new(4, true, macro)]);
        Assert.Equal(1, allocation.Sectors.Count);
        Assert.Equal(allocation.Bindings[(3, false)], allocation.Bindings[(4, true)]);
        Assert.Equal(true, allocation.Sectors.Values.All(b => SectorChecksum.IsValid(b)));
    }

    [Test]
    public void MacroEntriesFollowNativeTwoByteAlignment()
    {
        var snapshot = Fixture.Create(10);
        var first = MacroCompiler.Compile("tap A");
        var second = MacroCompiler.Compile("tap B");
        var allocation = MacroAllocator.Allocate(snapshot, [new(3, false, first), new(4, false, second)]);
        Assert.Equal(new MacroLocation(6, 0), allocation.Bindings[(3, false)]);
        Assert.Equal(new MacroLocation(6, 8), allocation.Bindings[(4, false)]);
        Assert.Equal((byte)0xFF, allocation.Sectors[6][7]);
        foreach (var sector in allocation.Sectors) snapshot.Sectors[sector.Key] = sector.Value;
        Assert.Equal(Convert.ToHexString(second.Bytes), Convert.ToHexString(MacroAllocator.ReadMacroBytes(snapshot, allocation.Bindings[(4, false)])));
    }

    [Test]
    public void ReimportMigratesOddEntryWithoutOverwritingSharedLegacySector()
    {
        var snapshot = Fixture.Create(10); var macro = MacroCompiler.Compile("tap A");
        var sector = Fixture.MacroSector([]); macro.Bytes.CopyTo(sector, 5); SectorChecksum.Seal(sector); snapshot.Sectors[6] = sector;
        foreach (var index in new[] { 0, 1 })
        {
            var profile = snapshot.GetProfiles()[index]; profile.SetBinding(3, false, ButtonBinding.Macro(6, 5)); snapshot.Sectors[profile.Sector] = profile.ToBytes();
        }
        var original = Convert.ToHexString(sector);
        var plan = MemoryWritePlan.Create(snapshot, snapshot.GetProfiles()[0], [new(3, false, macro)], true);
        Assert.Equal(2, plan.Changes.Count);
        Assert.Equal(true, plan.Changes.Any(c => c.Sector == 7));
        Assert.Equal(true, plan.Changes.All(c => c.Sector is 1 or 7));
        Assert.Equal(original, Convert.ToHexString(snapshot.Sectors[6]));
        foreach (var change in plan.Changes) snapshot.Sectors[change.Sector] = change.After;
        var changedProfile = snapshot.GetProfiles()[0];
        var binding = changedProfile.GetBinding(3);
        Assert.Equal((ushort)7, ButtonBinding.MacroSector(binding)); Assert.Equal((ushort)0, ButtonBinding.MacroOffset(binding));
    }

    [Test]
    public void AlignmentPaddingCannotPushAnEntryIntoChecksumBytes()
    {
        var snapshot = Fixture.Create(10);
        var first = MacroCompiler.Compile(string.Join('\n', Enumerable.Repeat("delay 1", 83)), MacroRepeat.WhileHeld);
        var allocation = MacroAllocator.Allocate(snapshot, [new(3, false, first), new(4, false, MacroCompiler.Compile("wait-release"))]);
        Assert.Equal(251, first.Length);
        Assert.Equal(new MacroLocation(7, 0), allocation.Bindings[(4, false)]);
        Assert.Equal(true, allocation.Sectors.Values.All(data => SectorChecksum.IsValid(data)));
    }

    [Test]
    public void LongMacroUsesJumpChainAtInstructionBoundaries()
    {
        var snapshot = Fixture.Create(); var macro = MacroCompiler.Compile(string.Join('\n', Enumerable.Repeat("tap A", 110)));
        var allocation = MacroAllocator.Allocate(snapshot, [new(3, false, macro)]);
        Assert.Equal(true, allocation.Sectors.Count > 1);
        foreach (var entry in allocation.Sectors) snapshot.Sectors[entry.Key] = entry.Value;
        var profile = snapshot.GetProfiles()[0]; var location = allocation.Bindings[(3, false)];
        profile.SetBinding(3, false, ButtonBinding.Macro(location.Sector, location.Offset)); snapshot.Sectors[1] = profile.ToBytes();
        var used = MacroAllocator.FindReferencedSectors(snapshot);
        Assert.Equal(true, allocation.Sectors.Keys.All(used.Contains));
    }

    [Test]
    public void ExistingJumpTargetsStayReserved()
    {
        var snapshot = Fixture.Create(); var profile = snapshot.GetProfiles()[0];
        profile.SetBinding(3, false, ButtonBinding.Macro(6)); snapshot.Sectors[1] = profile.ToBytes();
        snapshot.Sectors[6] = Fixture.MacroSector([0x60, 0, 7]);
        snapshot.Sectors[7] = Fixture.MacroSector([0x43, 0, 4, 0x44, 0, 4, 0xFF]);
        var allocation = MacroAllocator.Allocate(snapshot, [new(4, false, MacroCompiler.Compile("tap B"))]);
        Assert.Equal((ushort)8, allocation.Bindings[(4, false)].Sector);
    }

    [Test]
    public void UnknownExistingMacroCannotBeOverwritten()
    {
        var snapshot = Fixture.Create(); var profile = snapshot.GetProfiles()[0];
        profile.SetBinding(3, false, ButtonBinding.Macro(6)); snapshot.Sectors[1] = profile.ToBytes();
        snapshot.Sectors[6] = Fixture.MacroSector([0xFE]);
        Reject<InvalidDataException>(() => MacroAllocator.Allocate(snapshot, [new(4, false, MacroCompiler.Compile("tap B"))]));
    }

    [Test]
    public void HugeMacroFailsWithoutChangingSnapshot()
    {
        var snapshot = Fixture.Create(); var original = Convert.ToHexString(snapshot.Sectors[6]);
        var macro = MacroCompiler.Compile(string.Join('\n', Enumerable.Repeat("tap A", 800)));
        Reject<InvalidOperationException>(() => MacroAllocator.Allocate(snapshot, [new(3, false, macro)]));
        Assert.Equal(original, Convert.ToHexString(snapshot.Sectors[6]));
    }

    [Test]
    public void NoChangeDoesNotScheduleFlashWrites()
    {
        var snapshot = Fixture.Create();
        Assert.Equal(0, MemoryWritePlan.Create(snapshot, snapshot.GetProfiles()[0], []).Changes.Count);
    }

    [Test]
    public void MacroBeforeProfileAndDirectoryLast()
    {
        var snapshot = Fixture.Create(); var profile = snapshot.GetProfiles()[1]; profile.Enabled = false;
        var plan = MemoryWritePlan.Create(snapshot, profile, [new(3, false, MacroCompiler.Compile("tap A"))]);
        Assert.Equal(6, plan.Changes[0].Sector);
        Assert.Equal(2, plan.Changes[1].Sector);
        Assert.Equal(0, plan.Changes[^1].Sector);
    }

    [Test]
    public void LeftClickAndEnabledProfileAreRequired()
    {
        var snapshot = Fixture.Create(); var profile = snapshot.GetProfiles()[0];
        profile.SetBinding(0, false, ButtonBinding.Disabled());
        Reject<InvalidDataException>(() => MemoryWritePlan.Create(snapshot, profile, []));
        snapshot.Sectors[0][6] = 0; SectorChecksum.Seal(snapshot.Sectors[0]);
        profile = snapshot.GetProfiles()[0]; profile.Enabled = false;
        Reject<InvalidOperationException>(() => MemoryWritePlan.Create(snapshot, profile, []));
    }

    [Test]
    public void Format10NeedsExplicitOptInAndPreservesUnconfirmedRegions()
    {
        var snapshot = Fixture.Create(format: 10); var profile = snapshot.GetProfiles()[0];
        Reject<NotSupportedException>(() => MemoryWritePlan.Create(snapshot, profile, []));
        var plan = MemoryWritePlan.Create(snapshot, profile, [new(3, false, MacroCompiler.Compile("tap A"))], true);
        var after = plan.Changes.Single(c => c.Sector == 1).After;
        for (var i = 0; i < after.Length - 2; i++)
            if (i is < 44 or >= 48) Assert.Equal(snapshot.Sectors[1][i], after[i]);
        profile.ReportInterval = 2;
        Reject<InvalidOperationException>(() => MemoryWritePlan.Create(snapshot, profile, [], true));
    }

    [Test]
    public void UnknownLayoutRemainsReadOnly()
    {
        var snapshot = Fixture.Create(format: 11);
        Assert.Equal(false, snapshot.Capabilities.CanEditProfiles);
        Assert.Equal(0, snapshot.GetProfiles().Count);
    }

    [Test]
    public void BackupIdentityMustMatchMouse()
    {
        var baseline = Fixture.Create(); var foreign = Fixture.Create(); foreign.DeviceIdentity = "another mouse";
        Reject<InvalidDataException>(() => MemoryWritePlan.Restore(baseline, foreign));
    }

    [Test]
    public async Task SectorReadsUseOverlappingTailFor255ByteMemory()
    {
        using var fake = new FakeMouse(Fixture.Create()); using var client = new HidPlusPlusClient(fake, 1);
        var memory = new OnboardMemory(client, "test-device", "Test Mouse");
        var snapshot = await memory.ReadAsync();
        Assert.Equal(true, fake.ReadOffsets.Contains(239));
        Assert.Equal(false, fake.ReadOffsets.Contains(240));
        Assert.Equal(5, snapshot.GetProfiles().Count);
    }

    [Test]
    public async Task RequestsIgnoreNotificationsAndUnrelatedReplies()
    {
        using var fake = new FakeMouse(Fixture.Create()) { InjectUnrelated = true };
        using var client = new HidPlusPlusClient(fake, 1);
        Assert.Equal(true, await client.PingAsync());
    }

    [Test]
    public async Task CommitBacksUpVerifiesAndActivatesOnboardProfile()
    {
        using var scratch = new ScratchDirectory(); var baseline = Fixture.Create();
        using var fake = new FakeMouse(baseline); using var client = new HidPlusPlusClient(fake, 1);
        var memory = new OnboardMemory(client, baseline.DeviceIdentity, baseline.DeviceName);
        var profile = baseline.GetProfiles()[0]; profile.Name = "Test profile";
        var plan = MemoryWritePlan.Create(baseline, profile, [new(3, false, MacroCompiler.Compile("tap A"))]);
        var result = await memory.CommitAsync(plan, scratch.Path);
        Assert.Equal(plan.Changes.Count, result.SectorsWritten); Assert.Equal((byte)1, fake.Mode); Assert.Equal((ushort)1, fake.Active);
        foreach (var change in plan.Changes) Assert.Equal(Convert.ToHexString(change.After), Convert.ToHexString(fake.Sectors[change.Sector]));
        Assert.Equal(true, File.Exists(result.BackupPath));
        var saved = await OnboardMemory.LoadBackupAsync(result.BackupPath);
        Assert.Equal(Convert.ToHexString(baseline.Sectors[1]), Convert.ToHexString(saved.Sectors[1]));
    }

    [Test]
    public async Task StaleSnapshotIsRejectedBeforeChangingModeOrFlash()
    {
        using var scratch = new ScratchDirectory(); var baseline = Fixture.Create();
        using var fake = new FakeMouse(baseline); using var client = new HidPlusPlusClient(fake, 1);
        var memory = new OnboardMemory(client, baseline.DeviceIdentity, baseline.DeviceName);
        var profile = baseline.GetProfiles()[0]; profile.Name = "New name";
        var plan = MemoryWritePlan.Create(baseline, profile, []);
        fake.Sectors[1][10] ^= 1;
        await RejectAsync<InvalidOperationException>(() => memory.CommitAsync(plan, scratch.Path));
        Assert.Equal(0, fake.WriteStarts); Assert.Equal((byte)2, fake.Mode);
    }

    [Test]
    public async Task FailedCommitRestoresPartialSectorAndOriginalHostMode()
    {
        using var scratch = new ScratchDirectory(); var baseline = Fixture.Create();
        using var fake = new FakeMouse(baseline) { FailOnceAtSector = 1 };
        using var client = new HidPlusPlusClient(fake, 1);
        var memory = new OnboardMemory(client, baseline.DeviceIdentity, baseline.DeviceName);
        var profile = baseline.GetProfiles()[0]; profile.Name = "Will rollback";
        var plan = MemoryWritePlan.Create(baseline, profile, [new(3, false, MacroCompiler.Compile("tap A"))]);
        var error = await RejectAsync<IOException>(() => memory.CommitAsync(plan, scratch.Path));
        Assert.Equal(true, error.Message.Contains("已读回验证并恢复"));
        foreach (var change in plan.Changes) Assert.Equal(Convert.ToHexString(change.Before), Convert.ToHexString(fake.Sectors[change.Sector]));
        Assert.Equal((byte)2, fake.Mode); Assert.Equal((ushort)0, fake.Active);
    }

    [Test]
    public async Task WriteEndHardwareErrorRequiresMatchingReadback()
    {
        using var scratch = new ScratchDirectory(); var baseline = Fixture.Create();
        using var fake = new FakeMouse(baseline) { FailOnceAtSector = 1, EndError = 4 };
        using var client = new HidPlusPlusClient(fake, 1);
        var memory = new OnboardMemory(client, baseline.DeviceIdentity, baseline.DeviceName);
        var profile = baseline.GetProfiles()[0]; profile.Name = "Readback wins";
        var result = await memory.CommitAsync(MemoryWritePlan.Create(baseline, profile, []), scratch.Path);
        Assert.Equal(1, result.SectorsWritten);
    }

    private static T Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T error) { return error; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
    private static async Task<T> RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T error) { return error; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}

internal static class Fixture
{
    internal static MemorySnapshot Create(byte format = 5)
    {
        var snapshot = new MemorySnapshot
        {
            DeviceIdentity = "test-device",
            DeviceName = "Test Mouse",
            Mode = 2,
            ActiveSector = 0,
            Description = [1, format, 1, 5, 2, 11, 16, 0, 255, 0x0A, 4, 0, 0, 0, 0, 0]
        };
        for (var i = 0; i < 16; i++) snapshot.Sectors[i] = Enumerable.Repeat((byte)0xFF, 255).ToArray();
        for (var i = 1; i <= 5; i++)
        {
            var data = snapshot.Sectors[i]; data[0] = format == 10 ? (byte)0xFF : (byte)1; data[1] = 2; data[2] = 0;
            ushort[] dpis = [800, 1200, 1600, 2400, 3200];
            for (var d = 0; d < 5; d++) System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(3 + d * 2), dpis[d]);
            for (var button = 0; button < 5; button++) ButtonBinding.Mouse(button + 1).CopyTo(data, 32 + button * 4);
            data[20] = 3; data[21] = 3;
            if (format == 10) for (var d = 0; d < 5; d++) System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(86 + d * 2), dpis[d]);
            SectorChecksum.Seal(data);
            snapshot.Sectors[0][4 * (i - 1)] = 0; snapshot.Sectors[0][4 * (i - 1) + 1] = (byte)i;
            snapshot.Sectors[0][4 * (i - 1) + 2] = (byte)(i <= 2 ? 1 : 0);
        }
        SectorChecksum.Seal(snapshot.Sectors[0]);
        snapshot.Sectors[256] = (byte[])snapshot.Sectors[0].Clone();
        snapshot.Sectors[257] = (byte[])snapshot.Sectors[1].Clone();
        snapshot.Sectors[258] = (byte[])snapshot.Sectors[2].Clone();
        return snapshot;
    }
    internal static byte[] MacroSector(byte[] program)
    {
        var sector = Enumerable.Repeat((byte)0xFF, 255).ToArray(); program.CopyTo(sector, 0); return SectorChecksum.Seal(sector);
    }
}

internal sealed class ScratchDirectory : IDisposable
{
    private readonly string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LogiBoardTests"));
    public string Path { get; }
    public ScratchDirectory() { Path = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
    public void Dispose()
    {
        var resolved = System.IO.Path.GetFullPath(Path);
        if (!resolved.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test scratch path.");
        Directory.Delete(resolved, recursive: true);
    }
}
