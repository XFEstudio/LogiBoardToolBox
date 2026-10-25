using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Lighting;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class AdvancedSettingsTests
{
    [Test]
    public async Task LightforceWritesOnlyItsMaskedBitAndSkipsUnchangedState()
    {
        using var mouse = new FeatureMouse(); using var client = new HidPlusPlusClient(mouse, 1);
        var controller = new AdvancedMouseController(client, "G502 X PLUS"); await controller.ReadAsync();
        await controller.SetLightforceAsync(false); Assert.Equal((byte)0xA0, mouse.ModeStatus);
        var write = mouse.Requests.Single(r => r[2] == 11 && r[3] >> 4 == 1);
        Assert.Equal("00A00001", Convert.ToHexString(write, 4, 4));
        await controller.SetLightforceAsync(false); Assert.Equal(1, mouse.Requests.Count(r => r[2] == 11 && r[3] >> 4 == 1));
        await controller.SetLightforceAsync(true); Assert.Equal((byte)0xA1, mouse.ModeStatus);
    }
    [Test]
    public async Task StartupTogglePreservesSixOpaqueBytesAndVerifiesReadback()
    {
        using var mouse = new FeatureMouse(); using var client = new HidPlusPlusClient(mouse, 1);
        var controller = new AdvancedMouseController(client, "G502 X PLUS");
        await controller.SetStartupEffectAsync(false);
        Assert.Equal(false, controller.State!.StartupEnabled!.Value);
        Assert.Equal("0B16212C3742", Convert.ToHexString(mouse.Startup, 4, 6));
        await controller.SetStartupEffectAsync(true); Assert.Equal(true, controller.State!.StartupEnabled!.Value);
        Assert.Equal(false, mouse.Requests.Any(r => r[2] == 12 && r[3] >> 4 is >= 6 and <= 8));
    }
    [Test]
    public async Task AdvertisedHeroWithFirmwareErrorCannotStartOrApplyCalibration()
    {
        using var mouse = new FeatureMouse { HeroUnavailable = true }; using var client = new HidPlusPlusClient(mouse, 1);
        var controller = new AdvancedMouseController(client, "G502 X PLUS"); var state = await controller.ReadAsync();
        Assert.Equal(false, state.CalibrationSupported); Assert.Equal(true, state.CalibrationIssue.Contains("0x05"));
        try { await controller.StartCalibrationAsync(); throw new Exception("Expected rejection"); } catch (NotSupportedException) { }
        try { await controller.SetCalibrationAsync(10); throw new Exception("Expected rejection"); } catch (NotSupportedException) { }
        Assert.Equal(false, mouse.Requests.Any(r => r[2] == 7 && r[3] >> 4 is 9 or 10));
    }
    [Test]
    public async Task HeroCalibrationInfoRequiresSoftwareModeAndBecomesAvailableAfterSwitch()
    {
        using var mouse = new FeatureMouse { Mode = 1 }; using var client = new HidPlusPlusClient(mouse, 1);
        var controller = new AdvancedMouseController(client, "G502 X PLUS"); var board = await controller.ReadAsync();
        Assert.Equal(false, board.CalibrationSupported); Assert.Equal(true, board.CalibrationIssue.Contains("板载模式"));
        Assert.Equal(false, mouse.Requests.Any(r => r[2] == 7 && r[3] >> 4 == 8));
        mouse.Mode = 2; var host = await controller.ReadAsync();
        Assert.Equal(true, host.CalibrationSupported); Assert.Equal((ushort)800, host.Calibration!.DpiX);
    }
    [Test]
    public async Task HitsSensorCannotExposeLightforceAndG502HasNoInventedLodMode()
    {
        using var mouse = new FeatureMouse { Hits = true }; using var client = new HidPlusPlusClient(mouse, 1);
        var controller = new AdvancedMouseController(client, "G502 X PLUS"); var state = await controller.ReadAsync();
        Assert.Equal(false, state.LightforceSupported); Assert.Equal(false, state.SurfaceModeSupported);
        try { await controller.SetLightforceAsync(false); throw new Exception("Expected rejection"); } catch (NotSupportedException) { }
        Assert.Equal(false, mouse.Requests.Any(r => r[2] == 11 && r[3] >> 4 == 1));
    }
    [Test]
    public async Task AutomaticSaveCoalescesAnEditingGesture()
    {
        var count = 0; await using var queue = new ImmediateSaveQueue(() => { count++; return Task.CompletedTask; }, TimeSpan.FromMilliseconds(10));
        for (var i = 0; i < 20; i++) queue.Request();
        await queue.FlushAsync(); Assert.Equal(1, count); Assert.Equal(false, queue.Pending);
    }
    [Test]
    public async Task EditsArrivingDuringFlashAreSerializedAndNotDropped()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0; var concurrent = 0; var maximum = 0;
        await using var queue = new ImmediateSaveQueue(async () => { maximum = Math.Max(maximum, ++concurrent); count++; if (count == 1) { entered.SetResult(); await release.Task; } concurrent--; }, TimeSpan.FromMilliseconds(1));
        queue.Request(); await entered.Task; queue.Request(); release.SetResult(); await queue.FlushAsync();
        Assert.Equal(2, count); Assert.Equal(1, maximum);
    }
    [Test]
    public async Task SaveFailureIsSurfacedAndANewEditCanRetry()
    {
        var count = 0; var failures = 0;
        await using var queue = new ImmediateSaveQueue(() => { if (++count == 1) throw new IOException("device disconnected"); return Task.CompletedTask; }, TimeSpan.FromMilliseconds(1));
        queue.Failed += (_, _) => failures++; queue.Request();
        try { await queue.FlushAsync(); throw new Exception("Expected failure"); } catch (IOException) { }
        queue.Request(); await queue.FlushAsync(); Assert.Equal(2, count); Assert.Equal(1, failures);
    }
    [Test]
    public async Task RestoreReturnsNewMacroSpaceToItsExactOriginalErasedBytes()
    {
        var original = Fixture.Create(10); using var mouse = new FakeMouse(original); using var client = new HidPlusPlusClient(mouse, 1);
        var memory = new OnboardMemory(client, original.DeviceIdentity, original.DeviceName); using var scratch = new ScratchDirectory();
        var plan = MemoryWritePlan.Create(original, original.GetProfiles()[0], [new(3, false, MacroCompiler.Compile("mouse 1 down\ndelay 20\nmouse 1 up"))], true);
        await memory.CommitAsync(plan, scratch.Path); var installed = await memory.ReadAsync();
        await memory.CommitAsync(MemoryWritePlan.Restore(installed, original, true), scratch.Path);
        await memory.SetModeAsync(false); var restored = await memory.ReadAsync();
        Assert.Equal(true, original.Sectors.All(p => p.Value.SequenceEqual(restored.Sectors[p.Key]))); Assert.Equal((byte)2, restored.Mode); Assert.Equal((ushort)0, restored.ActiveSector);
    }
    [Test]
    public void AutoSaveReusesExistingShortAndChainedMacros()
    {
        foreach (var script in new[] { "tap A", string.Join('\n', Enumerable.Repeat("tap B", 110)) })
        {
            var snapshot = Fixture.Create(10); var macro = MacroCompiler.Compile(script);
            var first = MemoryWritePlan.Create(snapshot, snapshot.GetProfiles()[0], [new(3, false, macro)], true);
            foreach (var change in first.Changes) snapshot.Sectors[change.Sector] = change.After;
            var next = MemoryWritePlan.Create(snapshot, snapshot.GetProfiles()[0], [new(3, false, macro)], true);
            Assert.Equal(0, next.Changes.Count);
        }
    }
    [Test]
    public void AnimationDirectionsAndHoldCurveProduceDistinctPlayback()
    {
        var red = Enumerable.Repeat(new Rgb(255, 0, 0), 8).ToArray(); var blue = Enumerable.Repeat(new Rgb(0, 0, 255), 8).ToArray();
        var settings = new LightingSettings { Mode = LightsyncMode.Animation, Period = 1000, Frames = [new(0, red), new(500, blue)] };
        Assert.Equal(new Rgb(128, 0, 128), LightingRenderer.Render(settings, 250)[0]);
        Assert.Equal(new Rgb(255, 0, 0), LightingRenderer.Render(settings with { Frames = [new(0, red, true, TransitionCurve.None), new(500, blue)] }, 250)[0]);
        var bounce = settings with { Direction = AnimationDirection.Bounce };
        Assert.Equal(LightingRenderer.Render(bounce, 750)[0], LightingRenderer.Render(bounce, 1250)[0]);
        var reversed = settings with { Direction = AnimationDirection.ReverseCycle };
        Assert.Equal(LightingRenderer.Render(settings, 100)[0], LightingRenderer.Render(reversed, 900)[0]);
        Assert.Equal(true, LightingTransition.Evaluate(TransitionCurve.InCirc, .25) < .25);
        Assert.Equal(true, LightingTransition.Evaluate(TransitionCurve.OutCirc, .25) > .25);
    }
    [Test]
    public void RandomAnimationKeepsEditedFrameDurationsAndReproduciblePreview()
    {
        var red = Enumerable.Repeat(new Rgb(255, 0, 0), 8).ToArray(); var blue = Enumerable.Repeat(new Rgb(0, 0, 255), 8).ToArray();
        var settings = new LightingSettings { Mode = LightsyncMode.Animation, Direction = AnimationDirection.Random, Period = 1000, Frames = [new(0, red, false), new(900, blue, false)] };
        Assert.Equal(red[0], LightingRenderer.Render(settings, 0)[0]); Assert.Equal(red[0], LightingRenderer.Render(settings, 899)[0]);
        Assert.Equal(blue[0], LightingRenderer.Render(settings, 900)[0]); Assert.Equal(blue[0], LightingRenderer.Render(settings, 999)[0]);
        Assert.Equal(red[0], LightingRenderer.Render(settings, 1000)[0]);
        Assert.Equal(LightingRenderer.Render(settings, 12345)[0], LightingRenderer.Render(settings, 12345)[0]);
    }
}
