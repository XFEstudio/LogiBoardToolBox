using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class NativeToggleFirmwareTests
{
    private const string Script = "mouse 1 down\ndelay 59\nmouse 1 up\ndelay 198";
    internal static MemorySnapshot Extended()
    {
        var snapshot = LegacyG502MacroTests.CapturedLayout();
        Convert.FromHexString("4C42540302").CopyTo(snapshot.Description, 11);
        return snapshot;
    }
    private static void Reject(Action action)
    {
        try { action(); } catch (NotSupportedException) { return; }
        throw new Exception("Unsupported firmware must never receive binding action 3.");
    }
    [Test]
    public void ExtensionRequiresTheExactRevisionMarkerAndMatchingFirmwareGeometry()
    {
        var description = Extended().Description;
        Assert.Equal(true, OnboardCapabilities.Parse(description).SupportsNativeToggle);
        Assert.Equal(false, OnboardCapabilities.Parse(description.AsSpan(0, 11)).SupportsNativeToggle);
        for (var i = 0; i < 16; i++)
        {
            var wrong = description.ToArray(); wrong[i] ^= 1;
            Assert.Equal(false, OnboardCapabilities.Parse(wrong).SupportsNativeToggle);
        }
        var v1 = description.ToArray(); v1[6] = 15; v1[15] = 1;
        Assert.Equal(false, OnboardCapabilities.Parse(v1).SupportsNativeToggle);
        Assert.Equal(false, LegacyG502MacroTests.CapturedLayout().Capabilities.SupportsNativeToggle);
    }
    [Test]
    public void DeclaredFirmwareCompilesRecordedDelaysAndEmitsNativeToggleBinding()
    {
        var before = Extended(); var caps = before.Capabilities;
        var compiled = MacroCompiler.CompileProgram(Script, MacroRepeat.Toggle).ToOnboard(caps);
        Assert.Equal("41000140003B4200014000C603FF", Convert.ToHexString(compiled.Bytes));
        Assert.Equal(Convert.ToHexString(compiled.Bytes), Convert.ToHexString(MacroCompiler.Compile(Script, MacroRepeat.Toggle, caps).Bytes));
        var plan = MemoryWritePlan.Create(before, before.GetProfiles()[0], [new(3, false, compiled)]);
        Apply(before, plan);
        var binding = before.GetProfiles()[0].GetBinding(3);
        Assert.Equal(true, ButtonBinding.IsNativeToggle(binding));
        Assert.Equal(true, MacroAllocator.FindReferencedSectors(before).Contains(ButtonBinding.MacroSector(binding)));
        Assert.Equal(Convert.ToHexString(compiled.Bytes), Convert.ToHexString(MacroAllocator.ReadMacroBytes(before, new(ButtonBinding.MacroSector(binding), ButtonBinding.MacroOffset(binding)))));
        Assert.Equal("触发宏", ButtonBinding.DescribeForDisplay(binding));
        var stock = LegacyG502MacroTests.CapturedLayout().Capabilities;
        Reject(() => MacroCompiler.Compile(Script, MacroRepeat.Toggle, stock));
        Reject(() => ButtonBinding.NativeToggle(stock, 6));
        Reject(() => MacroAllocator.Allocate(LegacyG502MacroTests.CapturedLayout(), [new(3, false, compiled)]));
    }
    [Test]
    public void IdenticalToggleProgramsHavePerBindingAddressesAndOrdinaryEditsReuseThem()
    {
        var before = Extended(); var macro = MacroCompiler.Compile(Script, MacroRepeat.Toggle, before.Capabilities);
        MacroAssignment[] assignments = [new(3, false, macro), new(4, false, macro), new(3, true, macro)];
        var first = MemoryWritePlan.Create(before, before.GetProfiles()[0], assignments);
        Apply(before, first);
        var profile = before.GetProfiles()[0];
        var bindings = new[] { profile.GetBinding(3), profile.GetBinding(4), profile.GetBinding(3, true) };
        Assert.Equal(3, bindings.Select(Convert.ToHexString).Distinct().Count());
        profile.Name = "DPI edit"; profile.Dpis = [800, 1600, 3200, 6400, 12800];
        var next = MemoryWritePlan.Create(before, profile, assignments);
        Assert.Equal(false, next.Changes.Any(c => c.Sector > before.Capabilities.ProfileCount));
        Apply(before, next); profile = before.GetProfiles()[0];
        Assert.Equal(Convert.ToHexString(bindings[0]), Convert.ToHexString(profile.GetBinding(3)));
        Assert.Equal(Convert.ToHexString(bindings[1]), Convert.ToHexString(profile.GetBinding(4)));
        Assert.Equal(Convert.ToHexString(bindings[2]), Convert.ToHexString(profile.GetBinding(3, true)));
    }
    [Test]
    public async Task NativeBindingAndMacroBytesRoundTripThroughTheCommitAndRestoreTransaction()
    {
        var before = Extended(); var macro = MacroCompiler.Compile(Script, MacroRepeat.Toggle, before.Capabilities);
        var plan = MemoryWritePlan.Create(before, before.GetProfiles()[0], [new(7, false, macro)]);
        using var scratch = new ScratchDirectory(); using var fake = new FakeMouse(before); using var client = new HidPlusPlusClient(fake, 1);
        var memory = new OnboardMemory(client, before.DeviceIdentity, before.DeviceName);
        await memory.CommitAsync(plan, scratch.Path); var actual = await memory.ReadAsync();
        Assert.Equal(true, actual.Capabilities.SupportsNativeToggle);
        var binding = actual.GetProfiles()[0].GetBinding(7); Assert.Equal((byte)0x30, binding[0]);
        Assert.Equal(Convert.ToHexString(macro.Bytes), Convert.ToHexString(MacroAllocator.ReadMacroBytes(actual, new(ButtonBinding.MacroSector(binding), ButtonBinding.MacroOffset(binding)))));
        await memory.CommitAsync(MemoryWritePlan.Restore(actual, before), scratch.Path);
        foreach (var sector in before.Sectors) Assert.Equal(Convert.ToHexString(sector.Value), Convert.ToHexString(fake.Sectors[sector.Key]));
    }
    [Test]
    public async Task FirmwareCapabilityChangingAfterPreparationPreventsAllWrites()
    {
        var before = Extended(); var macro = MacroCompiler.Compile(Script, MacroRepeat.Toggle, before.Capabilities);
        var plan = MemoryWritePlan.Create(before, before.GetProfiles()[0], [new(7, false, macro)]);
        var device = Extended(); Array.Clear(device.Description, 11, 5);
        using var scratch = new ScratchDirectory(); using var fake = new FakeMouse(device); using var client = new HidPlusPlusClient(fake, 1);
        var memory = new OnboardMemory(client, before.DeviceIdentity, before.DeviceName);
        try { await memory.CommitAsync(plan, scratch.Path); throw new Exception("Expected a changed-firmware failure."); }
        catch (InvalidOperationException) { }
        Assert.Equal(0, fake.WriteStarts);
    }
    [Test]
    public void StockBackupsRestoreToExtendedFirmwareButExtendedBackupsNeverRestoreToStock()
    {
        var stock = LegacyG502MacroTests.CapturedLayout(); var extended = Extended();
        _ = MemoryWritePlan.Restore(extended, stock);
        try { _ = MemoryWritePlan.Restore(stock, extended); throw new Exception("Expected incompatible firmware rejection."); }
        catch (InvalidDataException) { }
    }
    [Test]
    public void IncorrectNativeToggleProgramTerminationCannotReachTheWritePlan()
    {
        var before = Extended(); var incorrect = MacroCompiler.Compile(Script, MacroRepeat.WhileHeld) with { Repeat = MacroRepeat.Toggle };
        try { _ = MemoryWritePlan.Create(before, before.GetProfiles()[0], [new(3, false, incorrect)]); throw new Exception("Expected invalid termination rejection."); }
        catch (InvalidDataException) { }
    }
    private static void Apply(MemorySnapshot snapshot, MemoryWritePlan plan)
    {
        foreach (var change in plan.Changes) snapshot.Sectors[change.Sector] = change.After.ToArray();
    }
}
