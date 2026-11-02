using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class LegacyG502MacroTests
{
    // Sanitized captured payload: no device serial, user paths or private scripts.
    internal static MemorySnapshot CapturedLayout()
    {
        var result = Fixture.Create(2); result.Description = Convert.FromHexString("01020105050B1001000A010000000000");
        foreach (var sector in result.Sectors.Keys.ToArray())
        {
            var bytes = Enumerable.Repeat((byte)0xFF, 256).ToArray(); result.Sectors[sector].CopyTo(bytes, 0);
            result.Sectors[sector] = SectorChecksum.Seal(bytes);
        }
        for (var sector = 6; sector < 16; sector++) result.Sectors[sector] = new byte[256];
        result.Sectors[6] = Enumerable.Repeat((byte)0xFF, 256).ToArray();
        Convert.FromHexString("400032400032400032FF40000540000540000440000140002FFF400032400032400032FF40000540000540000440000140002FFF").CopyTo(result.Sectors[6], 0);
        var profile = result.GetProfiles()[1];
        profile.SetBinding(6, false, ButtonBinding.Macro(6)); profile.SetBinding(7, false, ButtonBinding.Macro(6, 10));
        profile.SetBinding(6, true, ButtonBinding.Macro(6, 26)); profile.SetBinding(7, true, ButtonBinding.Macro(6, 36));
        result.Sectors[2] = profile.ToBytes();
        profile = result.GetProfiles()[4]; profile.SetBinding(0, true, ButtonBinding.Macro(6, 52)); result.Sectors[5] = profile.ToBytes();
        return result;
    }

    [Test]
    public async Task CapturedNativePageWithFfffTrailerAndZeroFreePagesCanSaveNewMacro()
    {
        var before = CapturedLayout(); Assert.Equal(true, before.Capabilities.UsesLegacyMacroPages);
        Assert.Equal(false, SectorChecksum.IsValid(before.Sectors[6]));
        Assert.Equal("400032400032400032FF", Convert.ToHexString(MacroAllocator.ReadMacroBytes(before, new(6, 0))));
        Assert.Equal(true, MacroAllocator.FindReferencedSectors(before).SetEquals([6]));
        var macro = MacroCompiler.WithStandardDelay(MacroCompiler.Compile("key D down\nkey D up"), 50);
        var plan = MemoryWritePlan.Create(before, before.GetProfiles()[0], [new(3, false, macro)]);
        Assert.Equal(7, plan.Changes[0].Sector); Assert.Equal(false, plan.Changes.Any(c => c.Sector == 6));
        using var scratch = new ScratchDirectory(); using var mouse = new FakeMouse(before); using var client = new HidPlusPlusClient(mouse, 1);
        var memory = new OnboardMemory(client, before.DeviceIdentity, before.DeviceName);
        var snapshot = await memory.ReadAsync();
        Assert.Equal(1, mouse.Requests.Count(r => r[2] == 2 && r[3] >> 4 == 5 && r[4] == 0 && r[5] == 6 && r[6] == 0 && r[7] == 0));
        await memory.CommitAsync(plan, scratch.Path); var actual = await memory.ReadAsync();
        Assert.Equal(Convert.ToHexString(before.Sectors[6]), Convert.ToHexString(actual.Sectors[6]));
        var binding = actual.GetProfiles()[0].GetBinding(3);
        Assert.Equal(Convert.ToHexString(macro.Bytes), Convert.ToHexString(MacroAllocator.ReadMacroBytes(actual, new(ButtonBinding.MacroSector(binding), ButtonBinding.MacroOffset(binding)))));
        await memory.CommitAsync(MemoryWritePlan.Restore(actual, before), scratch.Path);
        foreach (var sector in before.Sectors) Assert.Equal(Convert.ToHexString(sector.Value), Convert.ToHexString(mouse.Sectors[sector.Key]));
    }

    [Test]
    public void RawMacrosCanUseByte255AndLongProgramsUseFiveByteStorageJumps()
    {
        var before = CapturedLayout();
        var exactPage = MacroCompiler.Compile(string.Join('\n', Enumerable.Repeat("delay 1", 85)));
        Assert.Equal(256, exactPage.Length);
        var allocation = MacroAllocator.Allocate(before, [new(3, false, exactPage)]);
        Assert.Equal(1, allocation.Sectors.Count); Assert.Equal((byte)0xFF, allocation.Sectors[7][255]);
        var longMacro = MacroCompiler.Compile(string.Join('\n', Enumerable.Repeat("tap A", 110)));
        allocation = MacroAllocator.Allocate(before, [new(3, false, longMacro)]);
        Assert.Equal(true, allocation.Sectors.Count > 1);
        foreach (var sector in allocation.Sectors) before.Sectors[sector.Key] = sector.Value;
        var location = allocation.Bindings[(3, false)];
        Assert.Equal(Convert.ToHexString(longMacro.Bytes), Convert.ToHexString(MacroAllocator.ReadMacroBytes(before, location)));
        var page = before.Sectors[location.Sector]; var offset = 0;
        while (page[offset] != 0x60) offset += MacroCompiler.InstructionSize(page[offset]);
        Assert.Equal("6000080000", Convert.ToHexString(page.AsSpan(offset, 5)));
    }

    [Test]
    public async Task LegacyRawPageSupportDoesNotAllowInvalidProfilesUnknownInstructionsOrStaleWrites()
    {
        var before = CapturedLayout();
        Assert.Equal(false, before.Capabilities.IsValidSectorWrite(1, before.Sectors[6]));
        var modern = Fixture.Create(10); Assert.Equal(false, modern.Capabilities.IsRawMacroSector(6));
        Assert.Equal(false, modern.Capabilities.IsValidSectorWrite(6, before.Sectors[6]));
        before.Sectors[6][0] = 0xFE;
        try { _ = MacroAllocator.Allocate(before, [new(3, false, MacroCompiler.Compile("tap A"))]); throw new Exception("Expected unknown opcode rejection."); }
        catch (InvalidDataException) { }
        before = CapturedLayout(); var plan = MemoryWritePlan.Create(before, before.GetProfiles()[0], [new(3, false, MacroCompiler.Compile("tap A"))]);
        using var scratch = new ScratchDirectory(); using var mouse = new FakeMouse(before); using var client = new HidPlusPlusClient(mouse, 1);
        mouse.Sectors[6][0] ^= 1; var memory = new OnboardMemory(client, before.DeviceIdentity, before.DeviceName);
        try { await memory.CommitAsync(plan, scratch.Path); throw new Exception("Expected stale snapshot rejection."); }
        catch (InvalidOperationException) { }
        Assert.Equal(0, mouse.WriteStarts);
    }

    [Test]
    public void FriendlyLabelsDecodeCapturedConsumerAndNoActionWithoutChangingBytes()
    {
        foreach (var (hex, label) in new[] { ("800300EA", "音量减少"), ("800300E9", "音量增加"), ("9000FFFF", "禁用"), ("90000000", "禁用"), ("800300CD", "播放 / 暂停") })
        {
            var raw = Convert.FromHexString(hex); Assert.Equal(label, ButtonBinding.DescribeForDisplay(raw)); Assert.Equal(hex, Convert.ToHexString(raw));
        }
        foreach (var hex in new[] { "00060000", "8003FFFF", "90FE0000", "80010030", "77012345" })
            Assert.Equal(false, ButtonBinding.DescribeForDisplay(Convert.FromHexString(hex)).Contains("0x", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("板载宏 6:0", ButtonBinding.Describe(Convert.FromHexString("00060000")));
    }
}
