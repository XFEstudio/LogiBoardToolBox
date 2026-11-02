using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Onboard;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class OnboardDpiPreservationTests
{
    [Test]
    public async Task SingleMacroPageCanBeEditedWhileOtherBindingsKeepTheirOriginalBytes()
    {
        var snapshot = Fixture.Create(1);
        snapshot.Description = Convert.FromHexString("0101010101080304000A010000000000");
        snapshot.Sectors = snapshot.Sectors.Where(p => p.Key is < 3 or >= 256).ToDictionary(p => p.Key, p =>
        { var data = Enumerable.Repeat((byte)0xFF, 1024).ToArray(); p.Value.CopyTo(data, 0); return SectorChecksum.Seal(data); });
        snapshot.Sectors[0] = Enumerable.Repeat((byte)0xFF, 1024).ToArray();
        new byte[] { 0, 1, 1, 0 }.CopyTo(snapshot.Sectors[0], 0); SectorChecksum.Seal(snapshot.Sectors[0]);
        snapshot.Sectors[2] = Enumerable.Repeat((byte)0xFF, 1024).ToArray();
        var a = Core.Macros.MacroCompiler.Compile("tap A"); var b = Core.Macros.MacroCompiler.Compile("tap B");
        using var scratch = new ScratchDirectory(); using var mouse = new FakeMouse(snapshot); using var client = new HidPlusPlusClient(mouse, 1);
        var memory = new OnboardMemory(client, snapshot.DeviceIdentity, snapshot.DeviceName);
        await memory.CommitAsync(MemoryWritePlan.Create(snapshot, snapshot.GetProfiles()[0], [new(5, false, a), new(6, false, b)]), scratch.Path);
        var before = await memory.ReadAsync(); var oldBinding = before.GetProfiles()[0].GetBinding(5); var retained = before.GetProfiles()[0].GetBinding(6);
        var replacement = Core.Macros.MacroCompiler.Compile("tap C\ndelay 40\ntap D");
        var plan = MemoryWritePlan.Create(before, before.GetProfiles()[0], [new(5, false, replacement)]);
        Assert.Equal(true, plan.Changes.Any(c => c.Sector == 2));
        await memory.CommitAsync(plan, scratch.Path); var after = await memory.ReadAsync();
        var binding = after.GetProfiles()[0].GetBinding(5);
        Assert.Equal(Convert.ToHexString(replacement.Bytes), Convert.ToHexString(MacroAllocator.ReadMacroBytes(after, new(2, ButtonBinding.MacroOffset(binding)))));
        Assert.Equal(Convert.ToHexString(b.Bytes), Convert.ToHexString(MacroAllocator.ReadMacroBytes(after, new(2, ButtonBinding.MacroOffset(retained)))));
        Assert.Equal(Convert.ToHexString(a.Bytes), Convert.ToHexString(MacroAllocator.ReadMacroBytes(after, new(2, ButtonBinding.MacroOffset(oldBinding)))));
        var profile = after.GetProfiles()[0]; profile.Name = "DPI unchanged";
        var namePlan = MemoryWritePlan.Create(after, profile, [new(5, false, replacement)]);
        Assert.Equal(false, namePlan.Changes.Any(c => c.Sector == 2));
    }

    [Test]
    public void CapturedG402NativeDpiSurvivesUnrelatedEditsButNewUnsupportedValuesAreRejected()
    {
        var range = PerformanceController.DecodeRanges(Convert.FromHexString("00FCE0540FC00000"));
        var sensor = new SensorPerformance(false, 0, range, range, 3192, 3192, 0, 0, 0, [125, 250, 500, 1000], [], [], 1000, 0);
        Assert.Equal(false, range.Contains(400));
        ushort[] original = [400, 800, 1600, 3200, 0];
        sensor.ValidateProfileDpiEdits(original, original, original, original);
        ushort[] edited = [420, 800, 1600, 3200, 0]; sensor.ValidateProfileDpiEdits(edited, edited, original, original);
        edited[0] = 401;
        try { sensor.ValidateProfileDpiEdits(edited, edited, original, original); throw new Exception("Expected invalid edit rejection."); }
        catch (ArgumentOutOfRangeException) { }
        Assert.Equal((ushort)400, original[0]);
    }

    [Test]
    public void G402RawMacroLayoutDoesNotRelaxDirectoryOrProfileCrc()
    {
        var caps = OnboardCapabilities.Parse(Convert.FromHexString("0101010101080304000A010000000000"));
        var bytes = Enumerable.Repeat((byte)0xFF, 1024).ToArray();
        Assert.Equal(true, caps.UsesLegacyMacroPages); Assert.Equal(1024, caps.MacroPageCapacity); Assert.Equal(5, caps.MacroJumpSize);
        Assert.Equal(true, caps.IsValidSectorWrite(2, bytes)); Assert.Equal(false, caps.IsValidSectorWrite(1, bytes));
        Assert.Equal(false, caps.IsValidSectorWrite(0, bytes)); Assert.Equal(false, Fixture.Create().Capabilities.IsRawMacroSector(6));
    }
}
