using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Onboard;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class OnboardHandoffTests
{
    [Test]
    public async Task ReturningFromExternalHostStopsSpyBeforeEnteringOnboardWithoutWritingFlash()
    {
        var snapshot = Fixture.Create(10);
        using var mouse = new FakeMouse(snapshot) { SpySupported = true, SpyActive = true };
        using var client = new HidPlusPlusClient(mouse, 1);
        var memory = new OnboardMemory(client, snapshot.DeviceIdentity, snapshot.DeviceName);
        var map = mouse.SpyMap.ToArray();
        await memory.SetModeAsync(true);
        Assert.Equal(false, mouse.SpyActive); Assert.Equal((byte)1, mouse.Mode);
        Assert.Equal(0, mouse.WriteStarts); Assert.Equal(true, map.SequenceEqual(mouse.SpyMap));
        Assert.Equal(true, mouse.Requests.FindIndex(StopSpy) < mouse.Requests.FindIndex(EnterOnboard));
        Assert.Equal(true, snapshot.Sectors.All(p => p.Value.SequenceEqual(mouse.Sectors[p.Key])));
    }

    [Test]
    public async Task ReselectingAnActiveOnboardProfileAlsoStopsStaleSpy()
    {
        var snapshot = Fixture.Create(10);
        using var mouse = new FakeMouse(snapshot) { SpySupported = true };
        using var client = new HidPlusPlusClient(mouse, 1);
        await client.FeatureAsync(0x8100, 1, [1]); mouse.SpyActive = true; mouse.Requests.Clear();
        var memory = new OnboardMemory(client, snapshot.DeviceIdentity, snapshot.DeviceName);
        await memory.ActivateProfileAsync(1);
        Assert.Equal(false, mouse.SpyActive); Assert.Equal((ushort)1, mouse.Active);
        Assert.Equal(true, mouse.Requests.Any(StopSpy)); Assert.Equal(0, mouse.WriteStarts);
    }

    [Test]
    public async Task ImmediateSaveCompletesTheHostHandoffBeforeWritingTheEditedProfile()
    {
        var snapshot = Fixture.Create(10); var draft = snapshot.GetProfiles()[0].Copy(); draft.Dpis = [400, 800, 1600, 3200, 6400];
        using var mouse = new FakeMouse(snapshot) { SpySupported = true, SpyActive = true };
        using var client = new HidPlusPlusClient(mouse, 1);
        var memory = new OnboardMemory(client, snapshot.DeviceIdentity, snapshot.DeviceName);
        using var scratch = new ScratchDirectory();
        await memory.CommitAsync(MemoryWritePlan.Create(snapshot, draft, [], true), scratch.Path);
        var stop = mouse.Requests.FindIndex(StopSpy); var mode = mouse.Requests.FindIndex(EnterOnboard);
        var write = mouse.Requests.FindIndex(r => r[2] == 2 && r[3] >> 4 == 6);
        Assert.Equal(true, stop >= 0 && stop < mode && mode < write); Assert.Equal(false, mouse.SpyActive);
    }

    [Test]
    public async Task FailedSpyStopPreventsFlashWritesAndDevicesWithoutSpyRemainSupported()
    {
        var snapshot = Fixture.Create(10); var draft = snapshot.GetProfiles()[0].Copy(); draft.Dpis = [800, 1600, 3200, 6400, 12800];
        using var mouse = new FakeMouse(snapshot) { SpySupported = true, SpyActive = true, FailSpyStop = true };
        using var client = new HidPlusPlusClient(mouse, 1);
        var memory = new OnboardMemory(client, snapshot.DeviceIdentity, snapshot.DeviceName);
        using var scratch = new ScratchDirectory(); var rejected = false;
        try { await memory.CommitAsync(MemoryWritePlan.Create(snapshot, draft, [], true), scratch.Path); }
        catch (IOException) { rejected = true; }
        Assert.Equal(true, rejected); Assert.Equal(0, mouse.WriteStarts); Assert.Equal((byte)2, mouse.Mode);
        Assert.Equal(true, snapshot.Sectors.All(p => p.Value.SequenceEqual(mouse.Sectors[p.Key])));
        using var older = new FakeMouse(snapshot); using var olderClient = new HidPlusPlusClient(older, 1);
        await new OnboardMemory(olderClient, snapshot.DeviceIdentity, snapshot.DeviceName).SetModeAsync(true);
        Assert.Equal((byte)1, older.Mode); Assert.Equal(0, older.WriteStarts);
    }

    private static bool StopSpy(byte[] r) => r[2] == 3 && r[3] >> 4 == 2;
    private static bool EnterOnboard(byte[] r) => r[2] == 2 && r[3] >> 4 == 1 && r[4] == 1;
}
