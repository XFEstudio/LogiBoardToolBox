using System.Buffers.Binary;
using System.Globalization;
using LogiBoardToolBox.Core.Macros;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

/// <summary>
/// Differential checks against the real Lua interpreter. Only the oracle's Sleep is replaced by
/// a trace marker; input and delay ordering are compared with the actual firmware bytecode.
/// No Windows input sink, device connection, hooks, or wall-clock timing assertions are used.
/// </summary>
[TestFixture]
public class LuaConversionParityTests
{
    [Test]
    public async Task RecoilTableIpairsAndHelperReturnMatchTheLuaInterpreter()
    {
        await Parity("""
            local recoil = {
              { x = 0, y = 3, ms = 59 },
              { x = -1, y = 4, ms = 198 },
              { x = 2, y = 5, ms = 25 }
            }
            local function duration(row) return row.ms end
            function OnEvent(event, arg)
              if event ~= "MOUSE_BUTTON_PRESSED" or arg ~= 1 then return end
              for i, row in ipairs(recoil) do
                MoveMouseRelative(row.x, row.y)
                Sleep(duration(row))
              end
            end
            """);
    }

    [Test]
    public async Task FiniteWhileRepeatAndBreakPreserveIterationStateAndDelays()
    {
        await Parity("""
            function OnEvent(event, arg)
              if event == "MOUSE_BUTTON_PRESSED" and arg == 3 then
                local i = 0
                while i < 4 do
                  i = i + 1
                  if i == 3 then break end
                  MoveMouseRelative(i, 0)
                  Sleep(i * 2)
                end
                repeat
                  i = i - 1
                  MoveMouseRelative(0, i)
                  Sleep(1)
                until i <= 0
              end
            end
            """, 3);
    }

    [Test]
    public async Task RepeatUntilCanReadItsBodyLocalWithoutLeakingItOutsideTheLoop()
    {
        await Parity("""
            function OnEvent(event, arg)
              if event == "MOUSE_BUTTON_PRESSED" and arg == 1 then
                local n = 0
                repeat
                  local finished = n >= 2
                  n = n + 1
                  MoveMouseRelative(0, n)
                until finished
                MoveMouseRelative(n, 0)
              end
            end
            """);
    }

    [Test]
    public async Task PressAndReleaseEventsPreserveHeldModifierAcrossTheFirmwareBoundary()
    {
        var program = await Parity("""
            function OnEvent(event, arg)
              if arg ~= 3 then return end
              if event == "MOUSE_BUTTON_PRESSED" then
                PressKey("lctrl")
                Sleep(59)
              elseif event == "MOUSE_BUTTON_RELEASED" then
                PressAndReleaseKey("a")
                Sleep(25)
                ReleaseKey("lctrl")
              end
            end
            """, 3);
        Assert.Equal(MacroRepeat.Sequence, program.Repeat);
        Assert.Equal(1, program.ToOnboard().Instructions.Count(instruction => instruction[0] == 1));
    }

    [Test]
    public async Task ReleaseOnlyLuaActionWaitsForThePhysicalRelease()
    {
        var program = await Parity("""
            function OnEvent(event, arg)
              if event == "MOUSE_BUTTON_RELEASED" and arg == 1 then
                PressAndReleaseMouseButton(3)
                Sleep(10)
              end
            end
            """);
        Assert.Equal(MacroRepeat.Sequence, program.Repeat);
        Assert.Equal(0, program.Press.Count);
        Assert.Equal(true, program.Release.Count > 0);
    }

    [Test]
    public async Task LuaTruthAndShortCircuitDoNotEvaluateTheUnusedErrorHelper()
    {
        await Parity("""
            local function unused() error("short circuit was evaluated") end
            function OnEvent(event, arg)
              if event == "MOUSE_BUTTON_PRESSED" and arg == 1 then
                local x = 0 or 99
                local y = "" and 7
                local z = false and unused() or 3
                MoveMouseRelative(x, y + z)
                if not 0 then PressAndReleaseKey("c") else PressAndReleaseKey("a") end
                if (false or true) and not nil then Sleep(10) end
              end
            end
            """);
    }

    [Test]
    public async Task LocalHelperClosureDoBlockAndForShadowingKeepTheirLuaScope()
    {
        await Parity("""
            local function twice(n)
              local base = n
              local function add(v) return base + v end
              return add(n)
            end
            function OnEvent(event, arg)
              if event == "MOUSE_BUTTON_PRESSED" and arg == 1 then
                local value = twice(2)
                do
                  local value = 90
                  value = value + 1
                  MoveMouseRelative(0, value)
                end
                for value = 1, 2 do MoveMouseRelative(value, 0) end
                MoveMouseRelative(0, value)
              end
            end
            """);
    }

    [Test]
    public async Task DuplicatePressReleaseAndTapOfHeldInputsKeepInputOwnership()
    {
        await Parity("""
            function OnEvent(event, arg)
              if event == "MOUSE_BUTTON_PRESSED" and arg == 1 then
                PressKey("lctrl")
                PressKey("lctrl")
                PressAndReleaseKey("lctrl", "a")
                Sleep(59)
                PressAndReleaseKey("a", "a")
                ReleaseKey("lctrl")
                ReleaseKey("lctrl")
                PressMouseButton(1)
                PressAndReleaseMouseButton(1)
                Sleep(25)
                ReleaseMouseButton(1)
                ReleaseMouseButton(1)
              end
            end
            """);
    }

    [Test]
    public async Task PositiveDivisorModuloMatchesLuaIncludingNegativeDividend()
    {
        await Parity("""
            function OnEvent(event, arg)
              if event == "MOUSE_BUTTON_PRESSED" and arg == 3 then
                MoveMouseRelative(-3 % 2, 3 % 2)
                MoveMouseRelative(-5 % 3, 5 % 3)
              end
            end
            """, 3);
    }

    [Test]
    public async Task FloorModuloPowerPrecedenceAndMathHelpersMatchLuaNumbers()
    {
        await Parity("""
            function OnEvent(event, arg)
              if event == "MOUSE_BUTTON_PRESSED" and arg == 3 then
                MoveMouseRelative(-3 % 2, 3 % 2)
                MoveMouseRelative(math.floor(-1.2), math.ceil(-1.2))
                Sleep(math.max(0, math.abs(-7)))
                MoveMouseRelative(2 ^ 3, math.min(4, 9))
                MoveMouseRelative(-2 ^ 2, 2 ^ 3 ^ 2)
              end
            end
            """, 3);
    }

    [Test]
    public async Task NegativeDivisorModuloIsExplicitlyRejectedWhileTheStandardFormulaWorks()
    {
        // MoonSharp 2.0.0's ExecMod uses IEEE remainder and gives 3 % -2 == -3.
        // Lua 5.2 specifies a - floor(a/b)*b, which gives -1. Do not burn that defect into firmware.
        // Primary sources and a raw-interpreter reproduction are in work/lua-conversion-parity/modulo-audit.json.
        foreach (var expression in new[] { "3 % -2", "-3 % -2" })
        {
            var error = Rejected("function OnEvent(event,arg) if event=='MOUSE_BUTTON_PRESSED' then MoveMouseRelative(0," + expression + ") end end");
            Assert.Equal(true, error.Message.Contains("负除数", StringComparison.Ordinal));
            Assert.Equal(true, error.Message.Contains("软件", StringComparison.Ordinal));
        }
        await Parity("""
            function OnEvent(event, arg)
              if event == "MOUSE_BUTTON_PRESSED" and arg == 3 then
                MoveMouseRelative(3 - math.floor(3 / -2) * -2, -3 - math.floor(-3 / -2) * -2)
              end
            end
            """, 3);
    }

    [Test]
    public async Task LongSleepChunkingPreservesTotalDelayAndActionBoundaries()
    {
        await Parity("""
            function OnEvent(event, arg)
              if event == "MOUSE_BUTTON_PRESSED" and arg == 1 then
                PressMouseButton(2)
                Sleep(70000)
                Sleep(0)
                Sleep(59)
                ReleaseMouseButton(2)
                Sleep(198)
                MoveMouseWheel(-1)
              end
            end
            """);
    }

    [Test]
    public void PersistentTableMutationAndDisabledPrimaryTriggerAreNotGuessed()
    {
        Reject("""
            local row = { y = 1 }
            function OnEvent(event, arg)
              if event == "MOUSE_BUTTON_PRESSED" and arg == 1 then
                local alias = row
                alias.y = alias.y + 1
                MoveMouseRelative(0, alias.y)
              end
            end
            """);
        Reject("""
            EnablePrimaryMouseButtonEvents(false)
            function OnEvent(event, arg)
              if event == "MOUSE_BUTTON_PRESSED" and arg == 1 then PressAndReleaseKey("a") end
            end
            """);
    }

    [Test]
    public async Task FirmwareTraceDecoderMatchesTheExistingMacroRuntimeForInputOwnership()
    {
        var program = MacroCompiler.CompileProgram("tap Shift+A\nmouse 2 down\ndelay 7\nmouse 2 up\nwheel -1\npan 1\nmove -2 3", MacroRepeat.Once);
        var sink = new TraceSink();
        await using var runtime = new MacroRuntime(sink);
        var noDelays = program with { Press = program.Press.Where(instruction => instruction[0] != 0x40).ToArray() };
        await runtime.Press("parity-decoder", noDelays);
        var expected = FirmwareTrace(program).Where(step => step.Kind is not "delay" and not "event");
        Assert.Equal(Signature(expected), Signature(sink.Snapshot()));
    }

    private const string DelayMarker = "__LB_PARITY_DELAY__";
    private const string DoneMarker = "__LB_PARITY_DONE__";

    private static async Task<MacroProgram> Parity(string source, int trigger = 1)
    {
        var interpreted = await InterpreterTrace(source, trigger);
        var compiled = LuaMacroImport.ConvertToMacro(source, trigger);
        if (compiled.Repeat is not MacroRepeat.Once and not MacroRepeat.Sequence)
            throw new InvalidOperationException("Finite parity fixtures must not silently become repeating macros.");
        Assert.Equal(Signature(interpreted), Signature(FirmwareTrace(compiled)));
        return compiled;
    }

    private static async Task<IReadOnlyList<TraceStep>> InterpreterTrace(string source, int trigger)
    {
        var trace = new TraceSink();
        var pressedDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasedDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var faulted = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = new LuaMacroRuntime(trace);
        runtime.Faulted += (_, error) => faulted.TrySetResult(error);
        runtime.Logged += (_, text) =>
        {
            if (text.StartsWith(DelayMarker, StringComparison.Ordinal))
                trace.Append(new("delay", Milliseconds: long.Parse(text.AsSpan(DelayMarker.Length), CultureInfo.InvariantCulture)));
            else if (text == DoneMarker + "MOUSE_BUTTON_PRESSED") pressedDone.TrySetResult();
            else if (text == DoneMarker + "MOUSE_BUTTON_RELEASED") releasedDone.TrySetResult();
        };
        // Test-only instrumentation does not run through the converter. It provides a structural
        // clock for the real interpreter, so a 70-second Sleep needs no real delay or tolerance.
        var observedSource = """
            local __parity_log = OutputLogMessage
            Sleep = function(ms)
              assert(type(ms) == "number" and ms >= 0 and ms <= 600000 and ms == math.floor(ms))
              __parity_log("__LB_PARITY_DELAY__%d", ms)
            end
            """ + "\n" + source + "\n" + """
            local __parity_user_event = OnEvent
            function OnEvent(event, arg, family)
              __parity_user_event(event, arg, family)
              __parity_log("__LB_PARITY_DONE__%s", event)
            end
            """;
        await runtime.StartAsync(observedSource);
        trace.Append(new("event", Code: 1));
        runtime.MouseButton(trigger, true);
        await Completed(pressedDone.Task, faulted.Task);
        trace.Append(new("event", Code: 2));
        runtime.MouseButton(trigger, false);
        await Completed(releasedDone.Task, faulted.Task);
        await runtime.StopAsync();
        return trace.Snapshot();
    }

    private static async Task Completed(Task completed, Task<Exception> faulted)
    {
        // Timeout is solely a deadlock guard, never a timing assertion or delay measurement.
        var finished = await Task.WhenAny(completed, faulted).WaitAsync(TimeSpan.FromSeconds(8));
        if (finished == faulted) throw new InvalidOperationException("Lua parity oracle failed.", await faulted);
        await completed;
    }

    private static IReadOnlyList<TraceStep> FirmwareTrace(MacroProgram program)
    {
        var result = new List<TraceStep> { new("event", Code: 1) };
        var held = new HashSet<(MacroInputKind Kind, int Code)>();
        var releaseBoundary = false;
        void Input(MacroInputKind kind, int code, bool down = false, int y = 0)
        {
            if (kind is MacroInputKind.Key or MacroInputKind.MouseButton or MacroInputKind.Consumer)
            {
                if (down ? !held.Add((kind, code)) : !held.Remove((kind, code))) return;
            }
            result.Add(TraceStep.From(new(kind, code, down, y)));
        }
        foreach (var instruction in program.ToOnboard().Instructions)
        {
            switch (instruction[0])
            {
                case 1:
                    if (releaseBoundary) throw new InvalidOperationException("Duplicate firmware release boundary.");
                    result.Add(new("event", Code: 2)); releaseBoundary = true; break;
                case 0x40: result.Add(new("delay", Milliseconds: BinaryPrimitives.ReadUInt16BigEndian(instruction.AsSpan(1, 2)))); break;
                case 0x43 or 0x44:
                    var down = instruction[0] == 0x43;
                    if (down) for (var bit = 0; bit < 8; bit++) if ((instruction[1] & (1 << bit)) != 0) Input(MacroInputKind.Key, 224 + bit, true);
                    if (instruction[2] != 0) Input(MacroInputKind.Key, instruction[2], down);
                    if (!down) for (var bit = 7; bit >= 0; bit--) if ((instruction[1] & (1 << bit)) != 0) Input(MacroInputKind.Key, 224 + bit, false);
                    break;
                case 0x41 or 0x42:
                    var mask = BinaryPrimitives.ReadUInt16BigEndian(instruction.AsSpan(1, 2));
                    for (var bit = 0; bit < 16; bit++) if ((mask & (1 << bit)) != 0) Input(MacroInputKind.MouseButton, bit + 1, instruction[0] == 0x41);
                    break;
                case 0x45 or 0x46: Input(MacroInputKind.Consumer, BinaryPrimitives.ReadUInt16BigEndian(instruction.AsSpan(1, 2)), instruction[0] == 0x45); break;
                case 0x20 or 0x21: Input(instruction[0] == 0x20 ? MacroInputKind.Wheel : MacroInputKind.Pan, unchecked((sbyte)instruction[1])); break;
                case 0x61: Input(MacroInputKind.Move, BinaryPrimitives.ReadInt16BigEndian(instruction.AsSpan(1, 2)), y: BinaryPrimitives.ReadInt16BigEndian(instruction.AsSpan(3, 2))); break;
                case 0xFF: break;
                default: throw new InvalidOperationException("Unexpected firmware opcode in finite parity fixture: " + instruction[0]);
            }
        }
        if (!releaseBoundary) result.Add(new("event", Code: 2));
        if (held.Count != 0) throw new InvalidOperationException("Firmware parity trace leaves a held input.");
        return result;
    }

    private static string Signature(IEnumerable<TraceStep> steps)
    {
        var normalized = new List<TraceStep>();
        foreach (var step in steps)
        {
            if (step.Kind == "delay")
            {
                if (step.Milliseconds == 0) continue;
                if (normalized.LastOrDefault() is { Kind: "delay" } previous)
                { normalized[^1] = previous with { Milliseconds = previous.Milliseconds + step.Milliseconds }; continue; }
            }
            normalized.Add(step);
        }
        return string.Join('|', normalized.Select(step => step.Kind switch
        {
            "delay" => "delay " + step.Milliseconds.ToString(CultureInfo.InvariantCulture),
            "event" => step.Code == 1 ? "<pressed>" : "<released>",
            _ => $"{step.Kind} {step.Code} {(step.Down ? 1 : 0)} {step.Y}",
        }));
    }

    private static void Reject(string source) => _ = Rejected(source);

    private static Exception Rejected(string source)
    {
        try { LuaMacroImport.ConvertToMacro(source); }
        catch (Exception error) when (error is FormatException or NotSupportedException) { return error; }
        throw new InvalidOperationException("Cross-event or disabled-trigger Lua must not be guessed into a constant macro.");
    }

    private sealed record TraceStep(string Kind, int Code = 0, bool Down = false, int Y = 0, long Milliseconds = 0)
    {
        public static TraceStep From(MacroInput input) => new(input.Kind.ToString(), input.Code, input.Down, input.Y);
    }

    private sealed class TraceSink : IMacroInputSink
    {
        private readonly object sync = new();
        private readonly List<TraceStep> steps = [];
        public void Send(MacroInput input) => Append(TraceStep.From(input));
        public void Append(TraceStep step) { lock (sync) steps.Add(step); }
        public IReadOnlyList<TraceStep> Snapshot() { lock (sync) return steps.ToArray(); }
    }
}
