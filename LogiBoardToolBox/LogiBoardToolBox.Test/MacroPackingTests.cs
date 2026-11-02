using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class MacroPackingTests
{
    [Test]
    public void AllPagesReferencedCanStillStoreNewMacroAndPreserveEveryOldProgram()
    {
        var snapshot = Fixture.Create(); var profile = snapshot.GetProfiles()[0];
        var old = MacroCompiler.Compile("tap A");
        for (var page = 6; page < 16; page++)
        {
            snapshot.Sectors[page] = Fixture.MacroSector(old.Bytes);
            profile.SetBinding(page - 6, true, ButtonBinding.Macro((ushort)page));
        }
        snapshot.Sectors[1] = profile.ToBytes();
        var program = MacroCompiler.Compile(string.Join('\n', Enumerable.Repeat("tap B\ndelay 59", 95)));
        var allocation = MacroAllocator.Allocate(snapshot, [new(4, false, program)], 1);
        Assert.Equal(true, allocation.Sectors.Count > 1);
        var before = snapshot.Sectors.ToDictionary(p => p.Key, p => Convert.ToHexString(p.Value));
        foreach (var p in allocation.Sectors) snapshot.Sectors[p.Key] = p.Value;
        Assert.Equal(Convert.ToHexString(program.Bytes), Convert.ToHexString(MacroAllocator.ReadMacroBytes(snapshot, allocation.Bindings[(4, false)])));
        for (ushort page = 6; page < 16; page++)
            Assert.Equal(Convert.ToHexString(old.Bytes), Convert.ToHexString(MacroAllocator.ReadMacroBytes(snapshot, new(page, 0))));
        Assert.Equal(before[1], Convert.ToHexString(snapshot.Sectors[1]));
    }

    [Test]
    public void ReplacingNearCapacityMacroReclaimsExclusiveStorageButKeepsSharedProgram()
    {
        var snapshot = Fixture.Create();
        var old = MacroCompiler.Compile(string.Join('\n', Enumerable.Repeat("tap A", 360)));
        var first = MemoryWritePlan.Create(snapshot, snapshot.GetProfiles()[0], [new(3, false, old)]);
        foreach (var c in first.Changes) snapshot.Sectors[c.Sector] = c.After;
        var sharedProfile = snapshot.GetProfiles()[1];
        sharedProfile.SetBinding(4, false, snapshot.GetProfiles()[0].GetBinding(3));
        snapshot.Sectors[2] = sharedProfile.ToBytes();
        var current = snapshot.GetProfiles()[0];
        var smaller = MacroCompiler.Compile("tap B\ndelay 198");
        var replacement = MacroAllocator.Allocate(snapshot, [new(3, false, smaller)], 1);
        foreach (var c in replacement.Sectors) snapshot.Sectors[c.Key] = c.Value;
        var shared = sharedProfile.GetBinding(4);
        Assert.Equal(Convert.ToHexString(old.Bytes), Convert.ToHexString(MacroAllocator.ReadMacroBytes(snapshot, new(ButtonBinding.MacroSector(shared), ButtonBinding.MacroOffset(shared)))));
        sharedProfile.SetBinding(4, false, ButtonBinding.Mouse(5)); snapshot.Sectors[2] = sharedProfile.ToBytes();
        var larger = MacroCompiler.Compile(string.Join('\n', Enumerable.Repeat("tap B", 390)));
        var final = MacroAllocator.Allocate(snapshot, [new(3, false, larger)], 1);
        foreach (var c in final.Sectors) snapshot.Sectors[c.Key] = c.Value;
        Assert.Equal(Convert.ToHexString(larger.Bytes), Convert.ToHexString(MacroAllocator.ReadMacroBytes(snapshot, final.Bindings[(3, false)])));
    }

    [Test]
    public void CapacityFailureReportsBytesAndDoesNotMutateSnapshot()
    {
        var snapshot = Fixture.Create(); var bytes = snapshot.Sectors.ToDictionary(p => p.Key, p => Convert.ToHexString(p.Value));
        var large = MacroCompiler.Compile(string.Join('\n', Enumerable.Repeat("tap A", 2500)));
        MacroCapacityException? error = null;
        try { _ = MacroAllocator.Allocate(snapshot, [new(3, false, large)], 1); }
        catch (MacroCapacityException e) { error = e; }
        Assert.Equal(true, error is not null && error.RequiredProgramBytes > error.AvailableStorageBytes);
        foreach (var s in snapshot.Sectors) Assert.Equal(bytes[s.Key], Convert.ToHexString(s.Value));
    }
}
