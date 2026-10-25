using System.Collections.Concurrent;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Lighting;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Onboard;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class RuntimeTests
{
    private sealed class Sink : IMacroInputSink
    {
        public ConcurrentQueue<MacroInput> Events { get; } = new();
        public void Send(MacroInput input) => Events.Enqueue(input);
        public async Task WaitFor(Func<MacroInput, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(2000);
            while (!Events.Any(predicate)) await Task.Delay(1, timeout.Token);
        }
    }
    [Test]
    public async Task SequencePressOnceHoldRepeatsReleaseOnce()
    {
        var sink = new Sink(); await using var runtime = new MacroRuntime(sink);
        var program = MacroCompiler.CompileProgram("tap A", MacroRepeat.Sequence, "tap B\ndelay 10", "tap C");
        var task = runtime.Press("button", program);
        await sink.WaitFor(e => e.Code == 5 && e.Down); await Task.Delay(30); runtime.Release("button"); await task.WaitAsync(TimeSpan.FromSeconds(2));
        var events = sink.Events.ToArray();
        Assert.Equal(1, events.Count(e => e.Code == 4 && e.Down));
        Assert.Equal(true, events.Count(e => e.Code == 5 && e.Down) >= 2);
        Assert.Equal(1, events.Count(e => e.Code == 6 && e.Down));
        Assert.Equal(6, events[^1].Code); Assert.Equal(false, events[^1].Down);
    }
    [Test]
    public async Task EarlyReleaseStillFinishesPressThenRunsRelease()
    {
        var sink = new Sink(); await using var runtime = new MacroRuntime(sink);
        var task = runtime.Press("b", MacroCompiler.CompileProgram("delay 50\ntap A", MacroRepeat.Sequence, "tap B", "tap C"));
        runtime.Release("b"); await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("4,4,6,6", string.Join(',', sink.Events.Select(e => e.Code)));
    }
    [Test]
    public async Task InterruptingHoldReleasesTemporaryKeysBeforeReleasePhase()
    {
        var sink = new Sink(); await using var runtime = new MacroRuntime(sink);
        var task = runtime.Press("b", MacroCompiler.CompileProgram("key Ctrl down", MacroRepeat.Sequence, "mouse 1 down\ndelay 1000\nmouse 1 up", "key Ctrl up\ntap C"));
        await sink.WaitFor(e => e.Kind == MacroInputKind.MouseButton && e.Down); runtime.Release("b"); await task.WaitAsync(TimeSpan.FromSeconds(2));
        var events = sink.Events.ToArray();
        Assert.Equal(true, Array.FindIndex(events, e => e.Kind == MacroInputKind.MouseButton && !e.Down) < Array.FindIndex(events, e => e.Kind == MacroInputKind.Key && e.Code == 224 && !e.Down));
        Assert.Equal(events.Count(e => e.Down), events.Count(e => !e.Down));
    }
    [Test]
    public async Task WhileHeldCancelsLongDelayAndCleansHeldInputs()
    {
        var sink = new Sink(); await using var runtime = new MacroRuntime(sink);
        var task = runtime.Press("b", MacroCompiler.CompileProgram("key A down\ndelay 65535\nkey A up", MacroRepeat.WhileHeld));
        await sink.WaitFor(e => e.Code == 4 && e.Down); runtime.Release("b"); await task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, sink.Events.Count); Assert.Equal(false, sink.Events.Last().Down);
    }
    [Test]
    public async Task ToggleStopsOnSecondPressAndOnceIgnoresRepeatedPress()
    {
        var sink = new Sink(); await using var runtime = new MacroRuntime(sink);
        var p = MacroCompiler.CompileProgram("tap A\ndelay 50", MacroRepeat.Toggle);
        var task = runtime.Press("toggle", p); await sink.WaitFor(e => e.Down); runtime.Release("toggle");
        Assert.Equal(false, task.IsCompleted); _ = runtime.Press("toggle", p); await task.WaitAsync(TimeSpan.FromSeconds(1));
        var once = MacroCompiler.CompileProgram("delay 50\ntap B", MacroRepeat.Once);
        var first = runtime.Press("once", once); var second = runtime.Press("once", once); Assert.Equal(true, ReferenceEquals(first, second)); await first;
        Assert.Equal(1, sink.Events.Count(e => e.Code == 5 && e.Down));
    }
    [Test]
    public async Task ConcurrentMacrosDoNotReleaseEachOthersModifiers()
    {
        var sink = new Sink(); await using var runtime = new MacroRuntime(sink);
        var a = runtime.Press("a", MacroCompiler.CompileProgram("key Ctrl down\ndelay 100\nkey Ctrl up", MacroRepeat.Once));
        await sink.WaitFor(e => e.Code == 224 && e.Down);
        var b = runtime.Press("b", MacroCompiler.CompileProgram("key Ctrl down\ndelay 300\nkey Ctrl up", MacroRepeat.Once));
        await a; Assert.Equal(0, sink.Events.Count(e => e.Code == 224 && !e.Down));
        await b; Assert.Equal(1, sink.Events.Count(e => e.Code == 224 && !e.Down));
    }
    [Test]
    public void SequenceWithoutHoldCompilesToWaitRelease()
    {
        var p = MacroCompiler.CompileProgram("key Ctrl down", MacroRepeat.Sequence, "", "key Ctrl up");
        Assert.Equal("4301E0014401E0FF", Convert.ToHexString(p.ToOnboard().Bytes));
        var seq = MacroCompiler.CompileProgram("tap A", MacroRepeat.Sequence, "tap B", "tap C");
        Reject<NotSupportedException>(() => seq.ToOnboard());
        Reject<FormatException>(() => MacroCompiler.CompileProgram("key A down", MacroRepeat.Sequence, "key A up", ""));
    }
    [Test]
    public void CapturedDpiRangesHandleStepWordsAcrossPageBoundaries()
    {
        var values = PerformanceController.DecodeRanges(Convert.FromHexString("0064E00100C8E00201F4E00503E8E00A07D0E03264000000"));
        Assert.Equal(100, values.Min()); Assert.Equal(25600, values.Max()); Assert.Equal(true, values.Contains(10800)); Assert.Equal(false, values.Contains(5001));
        Reject<InvalidDataException>(() => PerformanceController.DecodeRanges(Convert.FromHexString("E0000000")));
        Assert.Equal("125,250,500,1000", string.Join(',', PerformanceController.DecodeRateMask(0x0F)));
    }
    [Test]
    public void Format10PerformanceAndLightingPreserveExtensionSentinels()
    {
        var snapshot = Fixture.Create(10); var caps = snapshot.Capabilities; var p = snapshot.GetProfiles()[0];
        p.Dpis = [400, 800, 1600, 3200, 6400]; p.SetDpisY(caps, [800, 1200, 1600, 3200, 6400]);
        p.SetRateHz(caps, false, 1000); p.SetRateHz(caps, true, 500);
        p.SetLighting(0, OnboardLighting.Create(10, 20, 40, 60, 3000, 75));
        var plan = MemoryWritePlan.Create(snapshot, p, [], true); var data = plan.Changes.Single(c => c.Sector == 1).After;
        Assert.Equal((byte)0xFF, data[0]); Assert.Equal((byte)3, data[20]); Assert.Equal((byte)2, data[21]);
        foreach (var at in new[] { 13, 16, 22, 27, 84, 85, 148, 159, 252 }) Assert.Equal(snapshot.Sectors[1][at], data[at]);
        Assert.Equal(true, SectorChecksum.IsValid(data));
    }
    [Test]
    public void LightingPresetEncodingUsesDocumentedParameterOffsets()
    {
        Assert.Equal("0A1234560BB8014B000000", Convert.ToHexString(OnboardLighting.Create(10, 0x12, 0x34, 0x56, 3000, 75)));
        var signature = OnboardLighting.Create(15, 0, 0, 0, 4000, 100); Assert.Equal((byte)100, signature[8]); Assert.Equal((byte)0x0F, signature[6]); Assert.Equal((byte)0xA0, signature[7]);
        Assert.Equal("3,7,2,4,8,6,5,1", string.Join(',', LightsyncDevice.G502XZoneMap));
    }
    [Test]
    public void AnimationInterpolatesLoopsAndHoldsLastFrameWhenNonLooping()
    {
        var red = Enumerable.Repeat(new Rgb(255, 0, 0), 8).ToArray(); var blue = Enumerable.Repeat(new Rgb(0, 0, 255), 8).ToArray();
        var s = new LightingSettings { Mode = LightsyncMode.Animation, Period = 1000, Frames = [new(0, red), new(500, blue)] }; s.Validate();
        Assert.Equal(new Rgb(128, 0, 128), LightingRenderer.Render(s, 250)[0]);
        Assert.Equal(new Rgb(128, 0, 128), LightingRenderer.Render(s, 750)[0]);
        Assert.Equal(new Rgb(0, 0, 255), LightingRenderer.Render(s with { Loop = false }, 1500)[0]);
        Assert.Equal(new Rgb(0, 0, 0), LightingRenderer.Render(s with { Brightness = 0 }, 250)[0]);
    }
    [Test]
    public void FreestyleAndExternalSourcesProduceEightIndependentColors()
    {
        var colors = Enumerable.Range(0, 8).Select(i => Rgb.Hue(i / 8.0)).ToArray();
        var free = new LightingSettings { Mode = LightsyncMode.Freestyle, ZoneColors = colors };
        Assert.Equal(true, colors.SequenceEqual(LightingRenderer.Render(free, 0)));
        Assert.Equal(true, colors.SequenceEqual(LightingRenderer.Render(free with { Mode = LightsyncMode.Preset, Effect = LightingEffect.ScreenSampler }, 0, colors)));
        var audio = free with { Mode = LightsyncMode.Preset, Effect = LightingEffect.AudioVisualizer, Color = new(255, 0, 0), Background = default, AudioGain = 1 };
        Assert.Equal(4, LightingRenderer.Render(audio, 0, audio: .5).Count(c => c.R == 255));
        Reject<FormatException>(() => new LightingSettings { Regions = [new(0, 0, 2, 1)] }.Validate());
    }
    [Test]
    public async Task HostModeLeaseRestoresOriginalModeOnlyAfterLastOwner()
    {
        using var fake = new FakeMouse(Fixture.Create()); using var client = new HidPlusPlusClient(fake, 1);
        await client.FeatureAsync(0x8100, 1, [1]);
        var lease = new HostModeLease(client); await lease.AcquireAsync(); await lease.AcquireAsync();
        Assert.Equal((byte)2, fake.Mode); await lease.ReleaseAsync(); Assert.Equal((byte)2, fake.Mode);
        await lease.ReleaseAsync(); Assert.Equal((byte)1, fake.Mode); Assert.Equal((ushort)1, fake.Active); Assert.Equal(0, fake.WriteStarts);
    }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
    [Test]
    public void TapsInsideSequenceKeepPressModifiersHeld()
    {
        var p = MacroCompiler.CompileProgram("key Ctrl down\ntap Ctrl+C", MacroRepeat.Sequence, "tap Ctrl+V", "key Ctrl up");
        Assert.Equal("4301E0430006440006", Convert.ToHexString(p.Press.SelectMany(b => b).ToArray()));
        Assert.Equal("430019440019", Convert.ToHexString(p.Hold.SelectMany(b => b).ToArray()));
    }
    [Test]
    public async Task NativeModifierUsagesDoNotDuplicateSoftwareEventsOrReleaseHeldModifiers()
    {
        var program = MacroCompiler.CompileProgram("key Shift down\ntap Shift+A\nkey Shift up", MacroRepeat.Once);
        Assert.Equal("4302E14300044400044402E1FF", Convert.ToHexString(program.ToOnboard().Bytes));
        var sink = new Sink(); await using var runtime = new MacroRuntime(sink);
        await runtime.Press("native-modifier", program);
        Assert.Equal("225:True,4:True,4:False,225:False", string.Join(',', sink.Events.Select(e => $"{e.Code}:{e.Down}")));
        Assert.Equal("4301E04302E14401E04402E1FF", Convert.ToHexString(MacroCompiler.Compile("tap Ctrl+Shift").Bytes));
    }
    [Test]
    public void Format10ScrollBindingsCanAllocateMacrosWithoutTouchingYTable()
    {
        var s = Fixture.Create(10); var p = s.GetProfiles()[0];
        var plan = MemoryWritePlan.Create(s, p, [new(12, false, MacroCompiler.Compile("tap A"))], true);
        var changed = plan.Changes.Single(c => c.Sector == 1).After;
        Assert.Equal(true, ButtonBinding.IsMacro(changed.AsSpan(80, 4).ToArray()));
        Assert.Equal(true, changed.AsSpan(84, 12).SequenceEqual(s.Sectors[1].AsSpan(84, 12)));
    }
}
