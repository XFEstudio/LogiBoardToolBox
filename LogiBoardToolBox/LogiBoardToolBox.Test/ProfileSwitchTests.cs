using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Onboard;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class ProfileSwitchTests
{
    [Test]
    public async Task NonAdjacentPairWorksBothOrdersAndRestoresOriginalStorage()
    {
        foreach (var (a, b) in new[] { ((ushort)1, (ushort)4), ((ushort)5, (ushort)2) })
        {
            var before = Fixture.Create(); using var scratch = new ScratchDirectory();
            var setup = ProfileSwitchPlanner.Create(before, a, b, 3, 4, true);
            Assert.Equal(0, setup.Plan.Changes.Last().Sector);
            using var mouse = new FakeMouse(before); using var client = new HidPlusPlusClient(mouse, 1);
            var memory = new OnboardMemory(client, before.DeviceIdentity, before.DeviceName);
            await memory.CommitAsync(setup.Plan, scratch.Path);
            var saved = await memory.ReadAsync();
            Assert.Equal(true, ProfileSwitchPlanner.Matches(setup.Route, saved));
            Assert.Equal(2, saved.GetProfiles().Count(p => p.Enabled));
            foreach (var p in saved.GetProfiles().Where(p => p.Enabled))
            foreach (var shift in new[] { false, true })
            {
                var own = p.Sector == a ? 3 : 4; var other = own == 3 ? 4 : 3;
                var destination = own == 3 ? b : a;
                Assert.Equal(Convert.ToHexString(ButtonBinding.Disabled()), Convert.ToHexString(p.GetBinding(own, shift)));
                Assert.Equal(destination > p.Sector ? "9008FF00" : "9009FF00", Convert.ToHexString(p.GetBinding(other, shift)));
            }
            foreach (var s in before.Sectors.Keys.Where(s => s > before.Capabilities.ProfileCount || s >= 1 && s != a && s != b))
                Assert.Equal(Convert.ToHexString(before.Sectors[s]), Convert.ToHexString(saved.Sectors[s]));
            await memory.CommitAsync(ProfileSwitchPlanner.Remove(saved, setup.Route), scratch.Path);
            var restored = await memory.ReadAsync();
            foreach (var p in before.Sectors) Assert.Equal(Convert.ToHexString(p.Value), Convert.ToHexString(restored.Sectors[p.Key]));
            Assert.Equal(true, Directory.EnumerateFiles(scratch.Path, "*.json").Any());
        }
    }

    [Test]
    public void RemovePreservesLaterDpiAndKeyEditsAndReconfigureRetainsFirstOriginals()
    {
        var before = Fixture.Create();
        var first = ProfileSwitchPlanner.Create(before, 1, 4, 3, 4);
        var routed = Applied(before, first.Plan);
        var edited = routed.GetProfiles()[0]; edited.Name = "later DPI"; var dpis = edited.Dpis; dpis[0] = 1800; edited.Dpis = dpis;
        edited.SetBinding(7, false, ButtonBinding.Keyboard(0, 4)); routed.Sectors[1] = edited.ToBytes();
        var second = ProfileSwitchPlanner.Create(routed, 2, 5, 5, 6, previous: first.Route);
        var after = Applied(routed, second.Plan);
        Assert.Equal(true, ProfileSwitchPlanner.Matches(second.Route, after));
        var removed = Applied(after, ProfileSwitchPlanner.Remove(after, second.Route));
        Assert.Equal((ushort)1800, removed.GetProfiles()[0].Dpis[0]);
        Assert.Equal("later DPI", removed.GetProfiles()[0].Name);
        Assert.Equal(Convert.ToHexString(ButtonBinding.Keyboard(0, 4)), Convert.ToHexString(removed.GetProfiles()[0].GetBinding(7)));
        Assert.Equal(Convert.ToHexString(before.GetProfiles()[0].GetBinding(3)), Convert.ToHexString(removed.GetProfiles()[0].GetBinding(3)));
        Assert.Equal("1,2", string.Join(',', removed.GetProfiles().Where(p => p.Enabled).Select(p => p.Sector)));
    }

    [Test]
    public async Task WriteFailureRollsBackBothProfilesAndDirectory()
    {
        var before = Fixture.Create(); var setup = ProfileSwitchPlanner.Create(before, 1, 4, 3, 4);
        using var scratch = new ScratchDirectory(); using var mouse = new FakeMouse(before) { FailOnceAtSector = 4 };
        using var client = new HidPlusPlusClient(mouse, 1); var memory = new OnboardMemory(client, before.DeviceIdentity, before.DeviceName);
        var failed = false;
        try { await memory.CommitAsync(setup.Plan, scratch.Path); } catch (IOException) { failed = true; }
        Assert.Equal(true, failed);
        foreach (var p in before.Sectors) Assert.Equal(Convert.ToHexString(p.Value), Convert.ToHexString(mouse.Sectors[p.Key]));
        Assert.Equal((byte)2, mouse.Mode);
    }

    [Test]
    public void GuardsPreservePrimaryKeysAndRouteButAllowOtherEdits()
    {
        var before = Fixture.Create();
        Reject(() => ProfileSwitchPlanner.Create(before, 1, 1, 3, 4));
        Reject(() => ProfileSwitchPlanner.Create(before, 1, 2, 0, 4));
        Reject(() => ProfileSwitchPlanner.Create(before, 1, 2, 3, 3));
        var setup = ProfileSwitchPlanner.Create(before, 1, 4, 3, 4);
        var routed = Applied(before, setup.Plan);
        var draft = routed.GetProfiles()[0]; draft.Name = "editable";
        ProfileSwitchPlanner.ValidateDraft(setup.Route, routed, draft);
        draft.SetBinding(3, false, ButtonBinding.Mouse(4));
        Reject(() => ProfileSwitchPlanner.ValidateDraft(setup.Route, routed, draft));
        var other = routed.GetProfiles()[1]; other.Enabled = true;
        Reject(() => ProfileSwitchPlanner.ValidateDraft(setup.Route, routed, other));
    }

    private static MemorySnapshot Applied(MemorySnapshot before, MemoryWritePlan plan)
    {
        var copy = new MemorySnapshot { Description = (byte[])before.Description.Clone(), DeviceName = before.DeviceName,
            DeviceIdentity = before.DeviceIdentity, Mode = 1, ActiveSector = plan.ActivateSector,
            Sectors = before.Sectors.ToDictionary(p => p.Key, p => (byte[])p.Value.Clone()) };
        foreach (var c in plan.Changes) copy.Sectors[c.Sector] = (byte[])c.After.Clone();
        return copy;
    }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException) { return; }
        throw new Exception("Expected invalid profile route to be rejected.");
    }
}
