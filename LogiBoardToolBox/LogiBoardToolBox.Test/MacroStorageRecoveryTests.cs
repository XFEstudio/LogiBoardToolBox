using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class MacroStorageRecoveryTests
{
    [Test]
    public async Task ExactBackupRepairsOnlyCrcAsPartOfNewMacroCommit()
    {
        var proof = WithMacro(); var before = WithMacro(); before.Sectors[6][254] ^= 1;
        var original = Convert.ToHexString(before.Sectors[6]);
        var newMacro = MacroCompiler.Compile("mouse 1 down\ndelay 59\nmouse 1 up\ndelay 198", MacroRepeat.WhileHeld);
        var plan = MemoryWritePlan.CreateRecoveringMacroCrc(before, before.GetProfiles()[0], [new(4, false, newMacro)], [proof]);
        Assert.Equal(3, plan.Changes.Count); Assert.Equal(6, plan.Changes[0].Sector);
        Assert.Equal(true, ReferenceEquals(before, plan.Baseline));
        Assert.Equal(original, Convert.ToHexString(before.Sectors[6]));
        Assert.Equal(true, plan.Changes[0].Before.AsSpan(0, 253).SequenceEqual(plan.Changes[0].After.AsSpan(0, 253)));
        Assert.Equal(true, plan.Summary.Contains("修改 3 个扇区"));
        using var scratch = new ScratchDirectory(); using var mouse = new FakeMouse(before);
        using var client = new HidPlusPlusClient(mouse, 1); var memory = new OnboardMemory(client, before.DeviceIdentity, before.DeviceName);
        await memory.CommitAsync(plan, scratch.Path); var actual = await memory.ReadAsync();
        Assert.Equal(Convert.ToHexString(proof.Sectors[6]), Convert.ToHexString(actual.Sectors[6]));
        Assert.Equal(Convert.ToHexString(MacroCompiler.Compile("tap A").Bytes), Convert.ToHexString(MacroAllocator.ReadMacroBytes(actual, new(6, 0))));
        var binding = actual.GetProfiles()[0].GetBinding(4);
        Assert.Equal(Convert.ToHexString(newMacro.Bytes), Convert.ToHexString(MacroAllocator.ReadMacroBytes(actual, new(ButtonBinding.MacroSector(binding), ButtonBinding.MacroOffset(binding)))));
        var saved = await OnboardMemory.LoadBackupAsync(Directory.GetFiles(scratch.Path, "*.json").Single());
        Assert.Equal(original, Convert.ToHexString(saved.Sectors[6]));
    }

    [Test]
    public void CrcRecoveryCannotGuessChangedPayloadOrUseAnotherDeviceBackup()
    {
        var before = WithMacro(); var proof = WithMacro(); before.Sectors[6][0] ^= 1;
        RejectRecovery(before, proof);
        before = WithMacro(); before.Sectors[6][254] ^= 1; proof.DeviceIdentity = "different mouse";
        RejectRecovery(before, proof);
        proof = WithMacro(); proof.Description[1] = 4; RejectRecovery(before, proof);
        proof = WithMacro(); proof.Sectors[6][254] ^= 1; RejectRecovery(before, proof);
    }

    [Test]
    public async Task FailureAfterCrcRepairRollsBackAllOriginalBytesIncludingInvalidCrc()
    {
        var before = WithMacro(); var proof = WithMacro(); before.Sectors[6][254] ^= 1;
        var plan = MemoryWritePlan.CreateRecoveringMacroCrc(before, before.GetProfiles()[0], [new(4, false, MacroCompiler.Compile("tap B"))], [proof]);
        using var mouse = new FakeMouse(before) { FailOnceAtSector = 1 }; using var client = new HidPlusPlusClient(mouse, 1);
        using var scratch = new ScratchDirectory(); var memory = new OnboardMemory(client, before.DeviceIdentity, before.DeviceName);
        try { await memory.CommitAsync(plan, scratch.Path); throw new Exception("Expected failure."); }
        catch (IOException error) { Assert.Equal(true, error.Message.Contains("恢复写入前")); }
        foreach (var sector in before.Sectors) Assert.Equal(Convert.ToHexString(sector.Value), Convert.ToHexString(mouse.Sectors[sector.Key]));
        Assert.Equal((byte)2, mouse.Mode);
    }

    private static MemorySnapshot WithMacro()
    {
        var before = Fixture.Create(); var profile = before.GetProfiles()[0];
        profile.SetBinding(3, false, ButtonBinding.Macro(6)); before.Sectors[1] = profile.ToBytes();
        before.Sectors[6] = Fixture.MacroSector(MacroCompiler.Compile("tap A").Bytes); return before;
    }
    private static void RejectRecovery(MemorySnapshot before, MemorySnapshot proof)
    {
        try { _ = MemoryWritePlan.CreateRecoveringMacroCrc(before, before.GetProfiles()[0], [new(4, false, MacroCompiler.Compile("tap B"))], [proof]); throw new Exception("Expected recovery rejection."); }
        catch (MacroStorageException) { }
    }

    [Test]
    public async Task ErasedReferencedPageIsReservedAndNewMacroCommitsWithoutChangingOldBinding()
    {
        var before = Fixture.Create();
        var profile = before.GetProfiles()[0]; profile.SetBinding(3, false, ButtonBinding.Macro(6)); before.Sectors[1] = profile.ToBytes();
        var disabled = before.GetProfiles()[2]; disabled.SetBinding(4, true, ButtonBinding.Macro(6, 40)); before.Sectors[3] = disabled.ToBytes();
        Assert.Equal("FF", Convert.ToHexString(MacroAllocator.ReadMacroBytes(before, new(6, 0))));
        var compiled = MacroCompiler.Compile("mouse 1 down\ndelay 59\nmouse 1 up\ndelay 198", MacroRepeat.WhileHeld);
        var plan = MemoryWritePlan.Create(before, profile, [new(4, false, compiled)]);
        Assert.Equal(true, plan.Changes.Any(c => c.Sector == 7));
        Assert.Equal(false, plan.Changes.Any(c => c.Sector == 6 || c.Sector == 3));
        using var scratch = new ScratchDirectory(); using var mouse = new FakeMouse(before);
        using var client = new HidPlusPlusClient(mouse, 1); var memory = new OnboardMemory(client, before.DeviceIdentity, before.DeviceName);
        await memory.CommitAsync(plan, scratch.Path); var actual = await memory.ReadAsync();
        Assert.Equal(true, SectorChecksum.IsErased(actual.Sectors[6]));
        Assert.Equal("00060000", Convert.ToHexString(actual.GetProfiles()[0].GetBinding(3)));
        var binding = actual.GetProfiles()[0].GetBinding(4);
        Assert.Equal(Convert.ToHexString(compiled.Bytes), Convert.ToHexString(MacroAllocator.ReadMacroBytes(actual,
            new(ButtonBinding.MacroSector(binding), ButtonBinding.MacroOffset(binding)))));
    }

    [Test]
    public void ErasedContinuationPageIsAlsoReserved()
    {
        var snapshot = Fixture.Create(); var profile = snapshot.GetProfiles()[0];
        profile.SetBinding(3, false, ButtonBinding.Macro(6)); snapshot.Sectors[1] = profile.ToBytes();
        var bytes = new byte[255]; bytes[252] = 0x40; snapshot.Sectors[6] = SectorChecksum.Seal(bytes);
        var used = MacroAllocator.FindReferencedSectors(snapshot);
        Assert.Equal(true, used.SetEquals([6, 7]));
        var next = MacroAllocator.Allocate(snapshot, [new(4, false, MacroCompiler.Compile("tap A"))]);
        Assert.Equal((ushort)8, next.Bindings[(4, false)].Sector);
    }

    [Test]
    public async Task TransientCrcFailureIsReadAgainBeforeNewMacroAllocation()
    {
        var before = Fixture.Create(); var profile = before.GetProfiles()[0];
        profile.SetBinding(3, false, ButtonBinding.Macro(6)); before.Sectors[1] = profile.ToBytes();
        before.Sectors[6] = Fixture.MacroSector(MacroCompiler.Compile("tap A").Bytes);
        using var mouse = new FakeMouse(before); var firstChunks = 0;
        mouse.ReadTransform = (sector, offset, chunk) => { if (sector == 6 && offset == 0 && ++firstChunks == 1) chunk[0] ^= 1; return chunk; };
        using var client = new HidPlusPlusClient(mouse, 1); var memory = new OnboardMemory(client, before.DeviceIdentity, before.DeviceName);
        var snapshot = await memory.ReadAsync();
        Assert.Equal(2, firstChunks); Assert.Equal(true, SectorChecksum.IsValid(snapshot.Sectors[6]));
        Assert.Equal(Convert.ToHexString(before.Sectors[6]), Convert.ToHexString(snapshot.Sectors[6]));
        var plan = MemoryWritePlan.Create(snapshot, snapshot.GetProfiles()[0], [new(4, false, MacroCompiler.Compile("tap B"))]);
        Assert.Equal(true, plan.Changes.Any(c => c.Sector == 7)); Assert.Equal(0, mouse.WriteStarts);
    }

    [Test]
    public async Task PersistentCorruptionIsNotResealedOrWrittenAndReportsBindingContext()
    {
        var before = Fixture.Create(); var profile = before.GetProfiles()[0];
        profile.SetBinding(3, false, ButtonBinding.Macro(6)); before.Sectors[1] = profile.ToBytes();
        before.Sectors[6] = Fixture.MacroSector(MacroCompiler.Compile("tap A").Bytes); before.Sectors[6][254] ^= 1;
        using var mouse = new FakeMouse(before); var reads = 0;
        mouse.ReadTransform = (sector, offset, chunk) => { if (sector == 6 && offset == 0) reads++; return chunk; };
        using var client = new HidPlusPlusClient(mouse, 1); var memory = new OnboardMemory(client, before.DeviceIdentity, before.DeviceName);
        var snapshot = await memory.ReadAsync(); Assert.Equal(3, reads);
        Assert.Equal(Convert.ToHexString(before.Sectors[6]), Convert.ToHexString(snapshot.Sectors[6]));
        try { _ = MemoryWritePlan.Create(snapshot, snapshot.GetProfiles()[0], [new(4, false, MacroCompiler.Compile("tap B"))]); throw new Exception("Expected storage error."); }
        catch (MacroStorageException error)
        {
            Assert.Equal(6, error.Sector); Assert.Equal(true, error.Message.Contains("CRC 不一致"));
            Assert.Equal("00060000", error.Data["Binding"] as string); Assert.Equal(3, (int)error.Data["ButtonIndex"]!);
        }
        Assert.Equal(0, mouse.WriteStarts);
    }

    [Test]
    public void MissingSectorIsDistinguishedFromCrcFailure()
    {
        var snapshot = Fixture.Create(); var profile = snapshot.GetProfiles()[0];
        profile.SetBinding(3, false, ButtonBinding.Macro(6)); snapshot.Sectors[1] = profile.ToBytes(); snapshot.Sectors.Remove(6);
        snapshot.ReadErrors[6] = "HID reply unavailable";
        try { _ = MacroAllocator.FindReferencedSectors(snapshot); throw new Exception("Expected missing page error."); }
        catch (MacroStorageException error) { Assert.Equal(true, error.Message.Contains("HID reply unavailable")); Assert.Equal(false, error.Message.Contains("CRC 不一致")); }
    }
}
