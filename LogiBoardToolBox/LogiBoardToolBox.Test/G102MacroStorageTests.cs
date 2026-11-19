using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class G102MacroStorageTests
{
    private static MemorySnapshot CapturedLayout()
    {
        var snapshot = Fixture.Create(4);
        snapshot.Description = Convert.FromHexString("0104010101061000FF0A010000000000");
        snapshot.DeviceName = "G102 LIGHTSYNC Gaming Mouse";
        snapshot.Mode = 1;
        snapshot.ActiveSector = 1;
        snapshot.Sectors.Remove(258);
        snapshot.Sectors[0].AsSpan(4).Fill(0xFF);
        SectorChecksum.Seal(snapshot.Sectors[0]);
        for (var sector = 2; sector < 16; sector++)
            snapshot.Sectors[sector].AsSpan().Fill(0xFF);

        // The captured format-4 macro page has a program followed by empty
        // entries at the six G-Shift addresses, with no CRC in the last two bytes.
        Convert.FromHexString("4301E040000D40000D40000D4401E0FF").CopyTo(snapshot.Sectors[2], 2);
        var profile = snapshot.GetProfiles().Single();
        for (var button = 0; button < 6; button++)
            profile.SetBinding(button, true, ButtonBinding.Macro(2, (ushort)(18 + button * 2)));
        snapshot.Sectors[1] = profile.ToBytes();
        return snapshot;
    }

    [Test]
    public async Task CapturedG102RawPageAllowsNewMacroSaveAndReadback()
    {
        var original = CapturedLayout();
        var payload = original.Sectors[2];
        Assert.Equal((ushort)0x8E61, SectorChecksum.Compute(payload.AsSpan(0, 253)));
        Assert.Equal(false, SectorChecksum.IsValid(payload));
        using var mouse = new FakeMouse(original);
        using var client = new HidPlusPlusClient(mouse, 1);
        using var scratch = new ScratchDirectory();
        var memory = new OnboardMemory(client, original.DeviceIdentity, original.DeviceName);
        var before = await memory.ReadAsync();
        var macro = MacroCompiler.Compile("wheel 1");
        var plan = MemoryWritePlan.Create(before, before.GetProfiles().Single(), [new(3, false, macro)]);
        Assert.Equal(true, plan.Changes.Select(c => c.Sector).SequenceEqual(new[] { 3, 1 }));
        Assert.Equal("FFFF", Convert.ToHexString(plan.Changes[0].After.AsSpan(253)));
        Assert.Equal(false, before.Capabilities.IsValidSectorWrite(1, payload));
        Assert.Equal(1, mouse.Requests.Count(r => r[2] == 2 && r[3] >> 4 == 5 && r[5] == 2 && r[6] == 0 && r[7] == 0));

        await memory.CommitAsync(plan, scratch.Path);
        var after = await memory.ReadAsync();
        var profile = after.GetProfiles().Single();
        var binding = profile.GetBinding(3);
        var location = new MacroLocation(ButtonBinding.MacroSector(binding), ButtonBinding.MacroOffset(binding));
        Assert.Equal("2001FF", Convert.ToHexString(MacroAllocator.ReadMacroBytes(after, location)));
        Assert.Equal("FF", Convert.ToHexString(MacroAllocator.ReadMacroBytes(after, new(2, 18))));
        Assert.Equal(true, payload.SequenceEqual(after.Sectors[2]));
        for (var button = 0; button < 6; button++)
            Assert.Equal(Convert.ToHexString(before.GetProfiles().Single().GetBinding(button, true)),
                Convert.ToHexString(profile.GetBinding(button, true)));
    }

    [Test]
    public void SealedSinglePageMacroFromPreviousVersionKeepsItsBindingAndBytes()
    {
        var snapshot = CapturedLayout();
        var existing = MacroCompiler.Compile("tap Ctrl+C\ndelay 80\ntap Ctrl+V");
        snapshot.Sectors[3] = Fixture.MacroSector(existing.Bytes);
        var profile = snapshot.GetProfiles().Single();
        profile.SetBinding(4, false, ButtonBinding.Macro(3));
        snapshot.Sectors[1] = profile.ToBytes();

        var plan = MemoryWritePlan.Create(snapshot, profile, [new(3, false, MacroCompiler.Compile("wheel 1"))]);
        Assert.Equal(true, plan.Changes.Select(c => c.Sector).SequenceEqual(new[] { 4, 1 }));
        var after = new MouseProfile(1, true, plan.Changes[^1].After, profileFormat: 4);
        Assert.Equal("00030000", Convert.ToHexString(after.GetBinding(4)));
        Assert.Equal(Convert.ToHexString(existing.Bytes), Convert.ToHexString(MacroAllocator.ReadMacroBytes(snapshot, new(3, 0))));
        var nearbyGeometry = (byte[])snapshot.Description.Clone();
        nearbyGeometry[10] ^= 1;
        Assert.Equal(false, OnboardCapabilities.Parse(nearbyGeometry).IsRawMacroSector(2));
    }
}
