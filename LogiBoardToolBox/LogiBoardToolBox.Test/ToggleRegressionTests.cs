using System.Collections.Concurrent;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class ToggleRegressionTests
{
    private sealed class Sink : IMacroInputSink
    {
        public ConcurrentQueue<MacroInput> Events { get; } = new();
        public void Send(MacroInput input) => Events.Enqueue(input);
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(3000);
        while (!condition()) await Task.Delay(1, timeout.Token);
    }
    private static void Reject(Action action)
    {
        try { action(); } catch (NotSupportedException error)
        { Assert.Equal(true, error.Message.Contains("同一按键")); return; }
        throw new InvalidOperationException("An uncancellable firmware loop must not be advertised as Toggle.");
    }
    [Test]
    public void ToggleIsNeverSilentlyCompiledToInfiniteOrWhileHeldFirmwareLoop()
    {
        const string script = "mouse 1 down\ndelay 200\nmouse 1 up\ndelay 200";
        Reject(() => MacroCompiler.Compile(script, MacroRepeat.Toggle));
        var program = MacroCompiler.CompileProgram(script, MacroRepeat.Toggle);
        Assert.Equal(MacroRepeat.Toggle, program.Repeat);
        Reject(() => program.ToOnboard());
        var handBuilt = new CompiledMacro([new byte[] { 0x41, 0, 1 }, new byte[] { 0x42, 0, 1 }, new byte[] { 3 }, new byte[] { 0xFF }], MacroRepeat.Toggle);
        Reject(() => MemoryWritePlan.Create(Fixture.Create(), Fixture.Create().GetProfiles()[0], [new(3, false, handBuilt)], true));
        Assert.Equal("4100014000C84200014000C802FF", Convert.ToHexString(MacroCompiler.Compile(script, MacroRepeat.WhileHeld).Bytes));
    }
    [Test]
    public async Task MouseClickToggleStopsDuringRecordedDelayAndDoesNotRestartOnRelease()
    {
        var sink = new Sink(); await using var runtime = new MacroRuntime(sink);
        var program = MacroCompiler.CompileProgram("mouse 1 down\ndelay 200\nmouse 1 up\ndelay 200", MacroRepeat.Toggle);
        var first = runtime.Press("G8", program);
        await Until(() => sink.Events.Count == 1);
        runtime.Release("G8");
        Assert.Equal(false, first.IsCompleted);
        var second = runtime.Press("G8", program);
        Assert.Equal(true, ReferenceEquals(first, second));
        await first.WaitAsync(TimeSpan.FromSeconds(1));
        runtime.Release("G8");
        Assert.Equal(2, sink.Events.Count);
        Assert.Equal(new MacroInput(MacroInputKind.MouseButton, 1, false), sink.Events.Last());
    }
    [Test]
    public async Task RapidStartStopStartKeepsThirdPressAndWaitsForReleaseCleanup()
    {
        var sink = new Sink(); await using var runtime = new MacroRuntime(sink);
        var program = MacroCompiler.CompileProgram("key A down\ndelay 65535\nkey A up", MacroRepeat.Toggle);
        var first = runtime.Press("G8", program);
        await Until(() => sink.Events.Count == 1);
        runtime.Release("G8");
        _ = runtime.Press("G8", program); runtime.Release("G8");
        var third = runtime.Press("G8", program); runtime.Release("G8");
        await Until(() => sink.Events.Count >= 3);
        Assert.Equal(false, ReferenceEquals(first, third));
        Assert.Equal("True,False,True", string.Join(',', sink.Events.Take(3).Select(e => e.Down)));
        _ = runtime.Press("G8", program);
        await third.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("True,False,True,False", string.Join(',', sink.Events.Select(e => e.Down)));
    }
    [Test]
    public async Task HostNotificationsToggleOnSecondPhysicalPressAndRestoreOriginalMapWithoutFlash()
    {
        using var mouse = new FeatureMouse { Mode = 1 };
        using var client = new HidPlusPlusClient(mouse, 1);
        var original = mouse.Map.ToArray(); var sink = new Sink();
        await using (var host = new HostMacroSession(client, sink))
        {
            var program = MacroCompiler.CompileProgram("mouse 1 down\ndelay 200\nmouse 1 up\ndelay 200", MacroRepeat.Toggle);
            await host.StartAsync([new(7, false, program)], -1);
            mouse.Notify(13, 128); await Until(() => sink.Events.Count == 1);
            mouse.Notify(13, 128); // Duplicate down notification is not another press.
            mouse.Notify(13, 0); await Task.Delay(20);
            Assert.Equal(1, sink.Events.Count);
            mouse.Notify(13, 128); await Until(() => sink.Events.Count == 2);
            mouse.Notify(13, 0);
        }
        Assert.Equal(true, mouse.Map.SequenceEqual(original));
        Assert.Equal((byte)1, mouse.Mode);
        Assert.Equal(false, mouse.Requests.Any(r => r[2] == 12 && r[3] >> 4 is >= 6 and <= 9));
        Assert.Equal("True,False", string.Join(',', sink.Events.Select(e => e.Down)));
    }
    [Test]
    public async Task DisposingActiveSoftwareToggleReleasesAllInputsBeforeReturningToOnboardMode()
    {
        using var mouse = new FeatureMouse { Mode = 1 };
        using var client = new HidPlusPlusClient(mouse, 1); var sink = new Sink();
        var original = mouse.Map.ToArray();
        var host = new HostMacroSession(client, sink);
        await host.StartAsync([new(7, false, MacroCompiler.CompileProgram("mouse 1 down\ndelay 65535\nmouse 1 up", MacroRepeat.Toggle))], -1);
        mouse.Notify(13, 128); await Until(() => sink.Events.Count == 1);
        await host.DisposeAsync();
        Assert.Equal("True,False", string.Join(',', sink.Events.Select(e => e.Down)));
        Assert.Equal(true, mouse.Map.SequenceEqual(original));
        Assert.Equal((byte)1, mouse.Mode);
    }
}
