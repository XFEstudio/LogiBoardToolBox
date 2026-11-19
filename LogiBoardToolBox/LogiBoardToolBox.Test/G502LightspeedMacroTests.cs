using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class G502LightspeedMacroTests
{
    private const string ExistingProgram = "43002C40008044002C4001F943002C40008044002C4000644301E04000004401E0FF";

    // Reproduces the supplied C08D report without its identifiers, profile
    // names, paths or factory firmware bytes. Only the relevant raw stream,
    // geometry and G-Shift reference at 6:34 are retained.
    internal static MemorySnapshot CapturedLayout()
    {
        var s = Fixture.Create(3);
        s.Description = Convert.FromHexString("01030105010B1000FF0A040000000000");
        s.Sectors.Remove(258);
        for (var i = 6; i < 16; i++) s.Sectors[i] = Enumerable.Repeat((byte)0xFF, 255).ToArray();
        var program = Convert.FromHexString(ExistingProgram);
        program.CopyTo(s.Sectors[6], 0); program.CopyTo(s.Sectors[6], 34);
        var profile = s.GetProfiles()[1]; profile.SetBinding(6, true, ButtonBinding.Macro(6, 34));
        s.Sectors[2] = profile.ToBytes();
        return s;
    }

    [Test]
    public void CapturedFfffPageIsAProgramRatherThanARepairableProfileCrc()
    {
        var s = CapturedLayout();
        Assert.Equal("FFFF", Convert.ToHexString(s.Sectors[6].AsSpan(253)));
        Assert.Equal((ushort)0x2299, SectorChecksum.Compute(s.Sectors[6].AsSpan(0, 253)));
        Assert.Equal(false, SectorChecksum.IsValid(s.Sectors[6]));
        Assert.Equal(ExistingProgram, Convert.ToHexString(MacroAllocator.ReadMacroBytes(s, new(6, 34))));
        Assert.Equal(true, MacroAllocator.FindReferencedSectors(s).SetEquals([6]));
        var macro = MacroCompiler.Compile("tap Ctrl+C\ndelay 80\ntap Ctrl+V");
        var plan = MemoryWritePlan.CreateRecoveringMacroCrc(s, s.GetProfiles()[0], [new(3, false, macro)], []);
        Assert.Equal(false, plan.Changes.Any(c => c.Sector == 6 || c.Purpose.Contains("恢复宏校验")));
        Assert.Equal(true, plan.Changes.Any(c => c.Sector == 7));
    }

    [Test]
    public async Task SaveReadbackAndRestorePreserveExistingGShiftMacroAndEveryOtherByte()
    {
        var s = CapturedLayout();
        using var mouse = new FakeMouse(s); using var client = new HidPlusPlusClient(mouse, 1);
        var memory = new OnboardMemory(client, s.DeviceIdentity, s.DeviceName);
        var before = await memory.ReadAsync();
        Assert.Equal(1, mouse.Requests.Count(r => r[2] == 2 && r[3] >> 4 == 5 && r[5] == 6 && r[6] == 0 && r[7] == 0));
        var macro = MacroCompiler.WithStandardDelay(MacroCompiler.Compile("mouse 1 down\nmouse 1 up"), 50);
        var plan = MemoryWritePlan.Create(before, before.GetProfiles()[0], [new(3, false, macro)]);
        using var scratch = new ScratchDirectory();
        await memory.CommitAsync(plan, scratch.Path);
        var after = await memory.ReadAsync();
        Assert.Equal(ExistingProgram, Convert.ToHexString(MacroAllocator.ReadMacroBytes(after, new(6, 34))));
        Assert.Equal("00060022", Convert.ToHexString(after.GetProfiles()[1].GetBinding(6, true)));
        var b = after.GetProfiles()[0].GetBinding(3);
        Assert.Equal(Convert.ToHexString(macro.Bytes), Convert.ToHexString(MacroAllocator.ReadMacroBytes(after,
            new(ButtonBinding.MacroSector(b), ButtonBinding.MacroOffset(b)))));
        foreach (var entry in before.Sectors.Where(e => !plan.Changes.Any(c => c.Sector == e.Key)))
            Assert.Equal(Convert.ToHexString(entry.Value), Convert.ToHexString(after.Sectors[entry.Key]));
        await memory.CommitAsync(MemoryWritePlan.Restore(after, before), scratch.Path);
        foreach (var entry in before.Sectors) Assert.Equal(Convert.ToHexString(entry.Value), Convert.ToHexString(mouse.Sectors[entry.Key]));
    }

    [Test]
    public void Full255BytePageDoesNotOverwriteLastInstructionWithAChecksum()
    {
        var s = CapturedLayout();
        var exact = MacroCompiler.Compile(string.Join('\n', Enumerable.Repeat("delay 1", 84)) + "\nwheel 1");
        Assert.Equal(255, exact.Length);
        var a = MacroAllocator.Allocate(s, [new(3, false, exact)]);
        Assert.Equal(1, a.Sectors.Count); Assert.Equal((ushort)7, a.Bindings[(3, false)].Sector);
        Assert.Equal(Convert.ToHexString(exact.Bytes), Convert.ToHexString(a.Sectors[7]));
    }

    [Test]
    public void ChainedFormatOneProgramUsesFullAddressAndPreservesExistingPage()
    {
        var s = CapturedLayout(); var original = Convert.ToHexString(s.Sectors[6]);
        var macro = MacroCompiler.Compile(string.Join('\n', Enumerable.Repeat("tap A", 120)));
        var allocation = MacroAllocator.Allocate(s, [new(3, false, macro)]);
        Assert.Equal(true, allocation.Sectors.Count > 1);
        foreach (var entry in allocation.Sectors) s.Sectors[entry.Key] = entry.Value;
        var location = allocation.Bindings[(3, false)];
        Assert.Equal(Convert.ToHexString(macro.Bytes), Convert.ToHexString(MacroAllocator.ReadMacroBytes(s, location)));
        var page = s.Sectors[location.Sector]; var offset = 0;
        while (page[offset] != 0x60) offset += MacroCompiler.InstructionSize(page[offset]);
        Assert.Equal("6000080000", Convert.ToHexString(page.AsSpan(offset, 5)));
        Assert.Equal(original, Convert.ToHexString(s.Sectors[6]));
    }

    [Test]
    public void RawPageRecognitionIsBoundedToCapturedGeometryAndNeverRelaxesProfileCrc()
    {
        var s = CapturedLayout(); var c = s.Capabilities;
        Assert.Equal(true, c.IsRawMacroSector(6)); Assert.Equal(true, c.IsRawMacroSector(15));
        foreach (var sector in new[] { -1, 0, 1, 5, 16, 256, 257 }) Assert.Equal(false, c.IsRawMacroSector(sector));
        Assert.Equal(false, c.IsValidSectorWrite(1, s.Sectors[6]));
        foreach (var field in new[] { 0, 1, 2, 3, 4, 5, 6, 8, 9 })
        {
            var changed = (byte[])s.Description.Clone(); changed[field] ^= 1;
            Assert.Equal(false, OnboardCapabilities.Parse(changed).IsRawMacroSector(6));
        }
        Assert.Equal(false, Fixture.Create(10).Capabilities.IsRawMacroSector(6));
        Assert.Equal(false, Fixture.Create(7).Capabilities.IsRawMacroSector(6));
        s.Sectors[6][34] = 0xFE;
        try { _ = MacroAllocator.Allocate(s, [new(3, false, MacroCompiler.Compile("tap A"))]); throw new Exception("Expected unknown opcode rejection"); }
        catch (InvalidDataException) { }
    }

    [Test]
    public async Task FailedProfileWriteRestoresTheExactRawPageAndErasedNewPage()
    {
        var s = CapturedLayout();
        var plan = MemoryWritePlan.Create(s, s.GetProfiles()[0], [new(3, false, MacroCompiler.Compile("tap Ctrl+C"))]);
        using var mouse = new FakeMouse(s) { FailOnceAtSector = 1 };
        using var client = new HidPlusPlusClient(mouse, 1); using var scratch = new ScratchDirectory();
        try { await new OnboardMemory(client, s.DeviceIdentity, s.DeviceName).CommitAsync(plan, scratch.Path); throw new Exception("Expected write failure"); }
        catch (IOException error) { Assert.Equal(true, error.Message.Contains("恢复写入前")); }
        foreach (var entry in s.Sectors) Assert.Equal(Convert.ToHexString(entry.Value), Convert.ToHexString(mouse.Sectors[entry.Key]));
        Assert.Equal((byte)2, mouse.Mode);
    }
}
