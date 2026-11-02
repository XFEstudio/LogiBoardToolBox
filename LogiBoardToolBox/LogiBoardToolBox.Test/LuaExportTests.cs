using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using LogiBoardToolBox.Core.Macros;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class LuaExportTests
{
    private sealed class Sink : IMacroInputSink
    {
        public ConcurrentQueue<MacroInput> Inputs { get; } = new();
        public void Send(MacroInput input) => Inputs.Enqueue(input);
        public async Task Wait(Func<MacroInput, bool> condition)
        { using var timeout = new CancellationTokenSource(3000); while (!Inputs.Any(condition)) await Task.Delay(1, timeout.Token); }
    }
    private static void Same(MacroProgram expected, MacroProgram actual)
    {
        Assert.Equal(expected.Repeat, actual.Repeat);
        Assert.Equal(Convert.ToHexString(expected.Press.SelectMany(b => b).ToArray()), Convert.ToHexString(actual.Press.SelectMany(b => b).ToArray()));
        Assert.Equal(Convert.ToHexString(expected.Hold.SelectMany(b => b).ToArray()), Convert.ToHexString(actual.Hold.SelectMany(b => b).ToArray()));
        Assert.Equal(Convert.ToHexString(expected.Release.SelectMany(b => b).ToArray()), Convert.ToHexString(actual.Release.SelectMany(b => b).ToArray()));
    }

    [Test]
    public void EveryRepeatModeExportsStandaloneLuaAndRoundTripsOriginalInstructions()
    {
        foreach (var mode in Enum.GetValues<MacroRepeat>())
        {
            var program = mode == MacroRepeat.Sequence
                ? MacroCompiler.CompileProgram("key Ctrl down\ndelay 59", mode, "mouse 2 down\ndelay 25\nmouse 2 up\ndelay 10", "key Ctrl up\ntap 1")
                : MacroCompiler.CompileProgram("key Ctrl+A down\ndelay 59\nkey Ctrl+A up\ndelay 198\nmouse 3 down\ndelay 25\nmouse 3 up\ndelay 10\nmove -3 4", mode);
            var source = LuaMacroExport.Export("导出宏\n--注释安全", program, 3);
            Assert.Equal(true, source.Contains("function OnEvent(event, arg)", StringComparison.Ordinal));
            Assert.Equal(true, source.Contains("__lb_key(30, true)", StringComparison.Ordinal) || mode == MacroRepeat.Sequence);
            Assert.Equal(true, source.Contains("__lb_button(2, true)", StringComparison.Ordinal) || mode == MacroRepeat.Sequence);
            Same(program, LuaMacroImport.ConvertToMacro(source, 3));
            Same(program, LuaMacroImport.ConvertToMacro(source.Replace("\n", "\r\n", StringComparison.Ordinal), 3));
        }
    }

    [Test]
    public void MetadataCannotOverrideEditedSourceOrInvalidChecksum()
    {
        var source = LuaMacroExport.Export("unchanged", MacroCompiler.CompileProgram("tap A\ndelay 59", MacroRepeat.Once));
        var editedCode = source.Replace("__lb_key(30, true)", "__lb_key(48, true)", StringComparison.Ordinal);
        Reject(() => LuaMacroImport.ConvertToMacro(editedCode));
        var lines = source.Split('\n');
        var metadataIndex = Array.FindIndex(lines, line => line.StartsWith("-- LBX-META:", StringComparison.Ordinal));
        var json = Convert.FromBase64String(lines[metadataIndex]["-- LBX-META:".Length..]);
        using var document = JsonDocument.Parse(json);
        var changedJson = Encoding.UTF8.GetString(json).Replace("unchanged", "modified", StringComparison.Ordinal);
        lines[metadataIndex] = "-- LBX-META:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(changedJson));
        Reject(() => LuaMacroImport.ConvertToMacro(string.Join('\n', lines)));
        Reject(() => LuaMacroImport.ConvertToMacro(source, 3));
    }

    [Test]
    public async Task ExportedOnceUsesCorrectRightMiddleScansAndReleasesInputs()
    {
        var sink = new Sink(); await using var runtime = new LuaMacroRuntime(sink);
        var source = LuaMacroExport.Export("right/middle", MacroCompiler.CompileProgram("tap Ctrl+A\nmouse 2 down\ndelay 1\nmouse 2 up\nmouse 3 down\nmouse 3 up", MacroRepeat.Once), 3);
        await runtime.StartAsync(source); runtime.MouseButton(3, true);
        await sink.Wait(e => e.Kind == MacroInputKind.MouseButton && e.Code == 3 && !e.Down);
        await runtime.StopAsync();
        Assert.Equal("224,4,4,224", string.Join(',', sink.Inputs.Where(e => e.Kind == MacroInputKind.Key).Select(e => e.Code)));
        Assert.Equal("2,2,3,3", string.Join(',', sink.Inputs.Where(e => e.Kind == MacroInputKind.MouseButton).Select(e => e.Code)));
        Assert.Equal(sink.Inputs.Count(e => e.Down), sink.Inputs.Count(e => !e.Down));
    }

    [Test]
    public async Task ExportedHeldMacroCancelsLongSleepWhenPhysicalButtonIsReleased()
    {
        var sink = new Sink(); await using var runtime = new LuaMacroRuntime(sink);
        var source = LuaMacroExport.Export("held", MacroCompiler.CompileProgram("key A down\ndelay 65535\nkey A up", MacroRepeat.WhileHeld));
        await runtime.StartAsync(source); runtime.MouseButton(1, true);
        await sink.Wait(e => e.Kind == MacroInputKind.Key && e.Code == 4 && e.Down);
        runtime.MouseButton(1, false);
        await sink.Wait(e => e.Kind == MacroInputKind.Key && e.Code == 4 && !e.Down);
        await runtime.StopAsync(); Assert.Equal(2, sink.Inputs.Count);
    }

    [Test]
    public async Task ExportedToggleConsumesItsStoppingPressInsteadOfRestartingQueuedEvent()
    {
        var sink = new Sink(); await using var runtime = new LuaMacroRuntime(sink);
        var source = LuaMacroExport.Export("toggle", MacroCompiler.CompileProgram("key A down\ndelay 65535\nkey A up", MacroRepeat.Toggle));
        await runtime.StartAsync(source); runtime.MouseButton(1, true);
        await sink.Wait(e => e.Kind == MacroInputKind.Key && e.Code == 4 && e.Down);
        runtime.MouseButton(1, false); await Task.Delay(30);
        runtime.MouseButton(1, true);
        await sink.Wait(e => e.Kind == MacroInputKind.Key && e.Code == 4 && !e.Down);
        runtime.MouseButton(1, false); await Task.Delay(30);
        Assert.Equal(1, sink.Inputs.Count(e => e.Code == 4 && e.Down));
        runtime.MouseButton(1, true);
        using (var timeout = new CancellationTokenSource(3000)) while (sink.Inputs.Count(e => e.Code == 4 && e.Down) != 2) await Task.Delay(1, timeout.Token);
        await runtime.StopAsync();
        Assert.Equal(2, sink.Inputs.Count(e => e.Code == 4 && !e.Down));
    }

    [Test]
    public async Task ExportedSequenceKeepsPressHeldModifiersAndReleasesTemporaryHoldInput()
    {
        var sink = new Sink(); await using var runtime = new LuaMacroRuntime(sink);
        var program = MacroCompiler.CompileProgram("key Ctrl down", MacroRepeat.Sequence, "mouse 1 down\ndelay 65535\nmouse 1 up", "key Ctrl up\ntap B");
        await runtime.StartAsync(LuaMacroExport.Export("sequence", program)); runtime.MouseButton(1, true);
        await sink.Wait(e => e.Kind == MacroInputKind.MouseButton && e.Down);
        runtime.MouseButton(1, false);
        await sink.Wait(e => e.Kind == MacroInputKind.Key && e.Code == 5 && !e.Down);
        await runtime.StopAsync(); var events = sink.Inputs.ToArray();
        Assert.Equal(true, Array.FindIndex(events, e => e.Kind == MacroInputKind.MouseButton && !e.Down) < Array.FindIndex(events, e => e.Kind == MacroInputKind.Key && e.Code == 224 && !e.Down));
        Assert.Equal(1, events.Count(e => e.Code == 224 && e.Kind == MacroInputKind.Key && e.Down));
    }

    [Test]
    public async Task ExportedWaitReleaseRemembersFirstReleaseEvenIfTriggerIsRepressed()
    {
        var sink = new Sink(); await using var runtime = new LuaMacroRuntime(sink);
        var program = MacroCompiler.CompileProgram("mouse 3 down\nmouse 3 up\ndelay 120\nwait-release\ntap A", MacroRepeat.Sequence, "", "tap B");
        await runtime.StartAsync(LuaMacroExport.Export("sticky release", program));
        runtime.MouseButton(1, true);
        await sink.Wait(e => e.Kind == MacroInputKind.MouseButton && e.Code == 3 && !e.Down);
        runtime.MouseButton(1, false); await Task.Delay(25); runtime.MouseButton(1, true);
        await sink.Wait(e => e.Kind == MacroInputKind.Key && e.Code == 5 && !e.Down);
        await runtime.StopAsync();
        Assert.Equal(1, sink.Inputs.Count(e => e.Kind == MacroInputKind.Key && e.Code == 4 && e.Down));
        Assert.Equal(1, sink.Inputs.Count(e => e.Kind == MacroInputKind.Key && e.Code == 5 && e.Down));
    }

    [Test]
    public void UnsupportedLuaOutputPrimitivesAreRejectedRatherThanDropped()
    {
        Reject(() => LuaMacroExport.Export("pan", MacroCompiler.CompileProgram("pan 1", MacroRepeat.Once)));
        Reject(() => LuaMacroExport.Export("media", MacroCompiler.CompileProgram("consumer 0xE9 down\nconsumer 0xE9 up", MacroRepeat.Once)));
        Reject(() => LuaMacroExport.Export("unknown button", MacroCompiler.CompileProgram("mouse 6 down\nmouse 6 up", MacroRepeat.Once)));
        Reject(() => LuaMacroExport.Export("unverified scan", MacroCompiler.CompileProgram("tap NumpadMultiply", MacroRepeat.Once)));
    }
    [Test]
    public void TenThousandStepsExportBeyondOldSourceLimitAndRoundTrip()
    {
        var program = new MacroProgram(MacroRepeat.Once, Enumerable.Range(0, 10000).Select(_ => new byte[] { 0x61, 0, 0, 0, 1 }).ToArray(), [], []);
        var source = LuaMacroExport.Export("large", program);
        Assert.Equal(true, source.Length > 262144);
        Same(program, LuaMacroImport.ConvertToMacro(source));
    }
    private static void Reject(Action action)
    { try { action(); } catch (Exception error) when (error is NotSupportedException or FormatException or InvalidDataException) { return; } throw new InvalidOperationException("Expected rejected Lua export/import."); }
}
