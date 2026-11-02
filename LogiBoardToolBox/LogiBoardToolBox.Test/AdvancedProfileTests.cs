using System.Buffers.Binary;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class AdvancedProfileTests
{
    [Test]
    public void CapturedFormat7ProfileDecodesAtItsActualOffsetsAndRetainsEveryByte()
    {
        var snapshot = Create(); var profile = snapshot.GetProfiles()[0]; var caps = snapshot.Capabilities;
        Assert.Equal(true, SectorChecksum.IsValid(CapturedProfile()));
        Assert.Equal((byte)7, profile.ProfileFormat);
        Assert.Equal("800,1200,1600,2400,3200", string.Join(',', profile.Dpis));
        Assert.Equal("800,1200,1600,2400,3200", string.Join(',', profile.GetDpisY(caps)));
        Assert.Equal((byte)0, profile.DefaultDpiIndex); Assert.Equal((byte)0, profile.ShiftDpiIndex);
        Assert.Equal(1000, profile.GetRateHz(caps, false)); Assert.Equal(1000, profile.GetRateHz(caps, true));
        Assert.Equal((ushort)60, profile.PowerSaveTimeout); Assert.Equal((ushort)300, profile.PowerOffTimeout);
        Assert.Equal("80010001", Convert.ToHexString(profile.GetBinding(0)));
        Assert.Equal(12, caps.BindingRecordCount); Assert.Equal(true, caps.HasIndependentAxes);
        Assert.Equal(false, caps.CanEditLighting);
        Assert.Equal(Convert.ToHexString(CapturedProfile()), Convert.ToHexString(profile.ToBytes()));
        Assert.Equal(Convert.ToHexString(profile.ToBytes()), Convert.ToHexString(profile.Copy().ToBytes()));
    }

    [Test]
    public void AdvancedProfileEditsPreserveLodPaddingAndOtherProfileBytes()
    {
        var snapshot = Create(); var caps = snapshot.Capabilities; var profile = snapshot.GetProfiles()[0];
        var before = profile.ToBytes();
        profile.Dpis = [400, 800, 2000, 4000, 6400];
        profile.SetDpisY(caps, [500, 1000, 2100, 4200, 6500]);
        profile.DefaultDpiIndex = 2; profile.ShiftDpiIndex = 1;
        profile.SetRateHz(caps, true, 4000); profile.SetRateHz(caps, false, 500);
        profile.PowerSaveTimeout = 90; profile.PowerOffTimeout = 600; profile.Name = "高级配置";
        profile.SetBinding(3, false, ButtonBinding.Mouse(5)); profile.SetBinding(4, true, ButtonBinding.Mouse(4));
        var plan = MemoryWritePlan.Create(snapshot, profile, []);
        var after = plan.Changes.Single(c => c.Sector == 1).After;
        Assert.Equal((byte)5, after[0]); Assert.Equal((byte)2, after[1]); Assert.Equal((byte)2, after[2]); Assert.Equal((byte)1, after[3]);
        Assert.Equal((ushort)2000, BinaryPrimitives.ReadUInt16LittleEndian(after.AsSpan(14)));
        Assert.Equal((ushort)2100, BinaryPrimitives.ReadUInt16LittleEndian(after.AsSpan(16)));
        Assert.Equal("80010010", Convert.ToHexString(after.AsSpan(60, 4)));
        Assert.Equal("80010008", Convert.ToHexString(after.AsSpan(128, 4)));
        Assert.Equal((ushort)90, BinaryPrimitives.ReadUInt16LittleEndian(after.AsSpan(44)));
        Assert.Equal((ushort)600, BinaryPrimitives.ReadUInt16LittleEndian(after.AsSpan(46)));
        for (var i = 0; i < 5; i++) Assert.Equal(before[8 + 5 * i], after[8 + 5 * i]);
        foreach (var offset in Enumerable.Range(29, 15).Concat(Enumerable.Range(96, 16)).Append(252)) Assert.Equal(before[offset], after[offset]);
        Assert.Equal(Convert.ToHexString(snapshot.Sectors[2]), Convert.ToHexString(plan.Baseline.Sectors[2]));
        Assert.Equal(true, SectorChecksum.IsValid(after));
    }

    [Test]
    public async Task Format7MacroWritePlansUseAdvancedBindingsAndRoundTripWithFullRestore()
    {
        var original = Create(); using var fake = new FakeMouse(original); using var client = new HidPlusPlusClient(fake, 1);
        var memory = new OnboardMemory(client, original.DeviceIdentity, original.DeviceName);
        var before = await memory.ReadAsync(); var profile = before.GetProfiles()[0].Copy();
        var compiled = MacroCompiler.Compile("mouse 1 down\ndelay 59\nmouse 1 up\ndelay 198");
        var plan = MemoryWritePlan.Create(before, profile, [new(3, false, compiled), new(4, true, compiled)]);
        using var scratch = new ScratchDirectory(); await memory.CommitAsync(plan, scratch.Path);
        var after = await memory.ReadAsync(); var actual = after.GetProfiles()[0];
        var normal = actual.GetBinding(3); var shifted = actual.GetBinding(4, true);
        Assert.Equal(true, ButtonBinding.IsMacro(normal)); Assert.Equal(Convert.ToHexString(normal), Convert.ToHexString(shifted));
        Assert.Equal(Convert.ToHexString(normal), Convert.ToHexString(actual.ToBytes().AsSpan(60, 4)));
        Assert.Equal(Convert.ToHexString(shifted), Convert.ToHexString(actual.ToBytes().AsSpan(128, 4)));
        var bytecode = MacroAllocator.ReadMacroBytes(after, new(ButtonBinding.MacroSector(normal), ButtonBinding.MacroOffset(normal)));
        Assert.Equal(Convert.ToHexString(compiled.Bytes), Convert.ToHexString(bytecode));
        Assert.Equal((byte)1, after.Mode);
        var restore = MemoryWritePlan.Restore(after, before); await memory.CommitAsync(restore, scratch.Path);
        var restored = await memory.ReadAsync();
        foreach (var pair in before.Sectors) Assert.Equal(Convert.ToHexString(pair.Value), Convert.ToHexString(restored.Sectors[pair.Key]));
    }

    [Test]
    public void UnknownAdvancedFieldsGeometryAndMacroFormatsCannotBeWritten()
    {
        foreach (var edit in new Action<byte[]>[] { d => d[5] = 13, d => d[8] = 254, d => d[3] = 6, d => d[4] = 6, d => d[9] = 3 })
        { var sample = Create(); edit(sample.Description); Assert.Equal(false, sample.Capabilities.CanEditProfiles); }
        var noMacro = Create(); noMacro.Description[2] = 2; Assert.Equal(false, noMacro.Capabilities.CanWriteMacros);
        foreach (var offset in new[] { 29, 33, 37, 43, 96, 108, 208, 219, 252, 8 })
        {
            var snapshot = Create(); var changed = snapshot.GetProfiles()[0].ToBytes(); changed[offset] ^= 1; SectorChecksum.Seal(changed);
            var profile = new MouseProfile(1, true, changed, profileFormat: 7);
            Reject<InvalidOperationException>(() => MemoryWritePlan.Create(snapshot, profile, []));
        }
        var source = Create(); var tooMany = source.GetProfiles()[0];
        Reject<ArgumentOutOfRangeException>(() => tooMany.SetBinding(12, false, ButtonBinding.Mouse(1)));
        Reject<NotSupportedException>(() => tooMany.SetLighting(0, new byte[11]));
        Reject<NotSupportedException>(() => tooMany.SetRateHz(source.Capabilities, false, 8000));
    }

    [Test]
    public void DisabledAdvancedDpiStagesCanBeEnabledWithoutChangingOtherLodValues()
    {
        var snapshot = Create(); var caps = snapshot.Capabilities; var profile = snapshot.GetProfiles()[0];
        profile.Dpis = [800, 1200, 1600, 2400, 0]; profile.SetDpisY(caps, [800, 1200, 1600, 2400, 0]);
        var plan = MemoryWritePlan.Create(snapshot, profile, []);
        var data = plan.Changes.Single(c => c.Sector == 1).After;
        Assert.Equal("0000000000", Convert.ToHexString(data.AsSpan(24, 5)));
        snapshot.Sectors[1] = data;
        profile = snapshot.GetProfiles()[0]; profile.Dpis = [800, 1200, 1600, 2400, 3200]; profile.SetDpisY(caps, [800, 1200, 1600, 2400, 3300]);
        plan = MemoryWritePlan.Create(snapshot, profile, []); data = plan.Changes.Single(c => c.Sector == 1).After;
        Assert.Equal((byte)2, data[28]); Assert.Equal((ushort)3300, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(26)));
    }

    internal static MemorySnapshot Create()
    {
        var snapshot = Fixture.Create(); snapshot.Description[1] = 7; snapshot.Description[5] = 5;
        snapshot.DeviceName = "PRO X SUPERLIGHT 2";
        for (var i = 1; i <= 5; i++) snapshot.Sectors[i] = CapturedProfile();
        snapshot.Sectors[257] = CapturedProfile(); snapshot.Sectors[258] = CapturedProfile();
        return snapshot;
    }

    // Protocol fixture: captured G HUB reset profile, CRC 84DB, published in
    // OpenMouse-Project/mouse-protocol onboard-profiles.ts (format 7). No physical I/O.
    private static byte[] CapturedProfile() => Convert.FromHexString(string.Concat("""
        03 03 00 00 20 03 20 03 02 b0 04 b0 04 02 40 06
        40 06 02 60 09 60 09 02 80 0c 80 0c 02 00 00 00
        00 ff 00 ff ff ff ff ff ff ff ff ff 3c 00 2c 01
        80 01 00 01 80 01 00 02 80 01 00 04 80 01 00 08
        80 01 00 10 ff ff ff ff ff ff ff ff ff ff ff ff
        ff ff ff ff ff ff ff ff ff ff ff ff ff ff ff ff
        ff ff ff ff ff ff ff ff ff ff ff ff ff ff ff ff
        ff ff ff ff ff ff ff ff ff ff ff ff ff ff ff ff
        ff ff ff ff ff ff ff ff ff ff ff ff ff ff ff ff
        ff ff ff ff ff ff ff ff ff ff ff ff ff ff ff ff
        ff ff ff ff ff ff ff ff ff ff ff ff ff ff ff ff
        ff ff ff ff ff ff ff ff ff ff ff ff ff ff ff ff
        ff ff ff ff ff ff ff ff ff ff ff ff ff ff ff ff
        03 00 00 00 00 00 1f 40 00 00 00 03 00 00 00 00
        00 1f 40 00 00 00 03 00 00 00 00 00 1f 40 32 00
        00 03 00 00 00 00 00 1f 40 32 00 00 03 84 db
        """.Where(c => !char.IsWhiteSpace(c))));

    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
