using System.Collections.Concurrent;
using System.Diagnostics;
using LogiBoardToolBox.Core.Macros;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class LuaMacroTests
{
    private sealed class Sink : IMacroInputSink
    {
        public ConcurrentQueue<MacroInput> Events { get; } = new();
        public void Send(MacroInput input) => Events.Enqueue(input);
        public async Task Wait(Func<MacroInput, bool> predicate)
        { using var timeout = new CancellationTokenSource(3000); while (!Events.Any(predicate)) await Task.Delay(1, timeout.Token); }
    }

    [Test]
    public void FiniteLuaConversionPreservesRecordedDelaysAndMouseNumbering()
    {
        var converted = LuaMacroImport.Convert("""
            EnablePrimaryMouseButtonEvents(true)
            function OnEvent(event, arg)
              if event == "MOUSE_BUTTON_PRESSED" and arg == 3 then
                PressMouseButton(3)
                Sleep(59)
                ReleaseMouseButton(3)
                Sleep(198)
                PressKey("1")
                Sleep(25)
                ReleaseKey("1")
                Sleep(10)
              end
            end
            """, 3);
        Assert.Equal("mouse 2 down\ndelay 59\nmouse 2 up\ndelay 198\nkey 1 down\ndelay 25\nkey 1 up\ndelay 10", converted.Script);
        Assert.Equal(MacroRepeat.Once, converted.Repeat);
        Assert.Equal(true, LuaMacroImport.ConvertToMacro("function OnEvent(event,arg) PressAndReleaseMouseButton(2) end").ToOnboard().Bytes.AsSpan().StartsWith(new byte[] { 0x41, 0, 4, 0x42, 0, 4 }));
    }

    [Test]
    public void FiniteForHelperAndBlockAssignmentsKeepLuaScopeAndValues()
    {
        var converted = LuaMacroImport.Convert("""
            local delay = 10
            function click(x)
              MoveMouseRelative(0, x)
              Sleep(delay)
            end
            function OnEvent(event, arg)
              local count = 1
              if arg == 1 then count = 2 end
              for i = 1, 3 do
                count = count + 1
                click(count)
              end
              MoveMouseRelative(0, count)
            end
            """);
        Assert.Equal("move 0 3\ndelay 10\nmove 0 4\ndelay 10\nmove 0 5\ndelay 10\nmove 0 5", converted.Script);
    }

    [Test]
    public void SelectedTriggerHoldLoopCompilesButToggleRequiresSoftware()
    {
        const string script = """
            function OnEvent(event,arg)
              if event == "MOUSE_BUTTON_PRESSED" and arg == 1 then
                repeat
                  MoveMouseRelative(0,4)
                  Sleep(11)
                until not IsMouseButtonPressed(1)
              end
            end
            """;
        Assert.Equal(MacroRepeat.WhileHeld, LuaMacroImport.Convert(script).Repeat);
        Assert.Equal("610000000440000B02FF", Convert.ToHexString(LuaMacroImport.ConvertToMacro(script).ToOnboard().Bytes));
        Assert.Equal(MacroRepeat.Toggle, LuaMacroImport.Convert(script, 1, MacroRepeat.Toggle).Repeat);
        Reject(() => LuaMacroImport.ConvertToMacro(script, 1, MacroRepeat.Toggle).ToOnboard());
    }

    [Test]
    public void OnboardConversionRejectsHostStateCrossEventVariablesAndChangingHoldBodies()
    {
        Reject(() => LuaMacroImport.Convert("function OnEvent(event,arg) if IsKeyLockOn('capslock') then PressAndReleaseKey('a') end end"));
        Reject(() => LuaMacroImport.Convert("local x=0 function OnEvent(event,arg) x=x+1 MoveMouseRelative(0,x) end"));
        Reject(() => LuaMacroImport.Convert("function OnEvent(event,arg) local x=0 while IsMouseButtonPressed(1) do x=x+1 MoveMouseRelative(0,x) end end"));
        Reject(() => LuaMacroImport.Convert("function OnEvent(event,arg) while IsMouseButtonPressed(3) do PressAndReleaseKey('a') end end", 1));
        Reject(() => LuaMacroImport.Convert("function OnEvent(event,arg) while true do end end"));
        Reject(() => LuaMacroImport.Convert("function OnEvent(event,arg) dofile('unknown.lua') end"));
        Reject(() => LuaMacroImport.Convert("PressAndReleaseKey('a') function OnEvent(event,arg) PressAndReleaseKey('b') end"));
    }

    [Test]
    public void ConverterPreservesShadowedLocalScopeAndBoundsEmptyLoopsAndNesting()
    {
        var converted = LuaMacroImport.Convert("function OnEvent(event,arg) local x=1 if true then local x=2 MoveMouseRelative(0,x) end for i=1,2 do local x=i MoveMouseRelative(0,x) end MoveMouseRelative(0,x) end");
        Assert.Equal("move 0 2\nmove 0 1\nmove 0 2\nmove 0 1", converted.Script);
        Reject(() => LuaMacroImport.Convert("local x=1 function helper() MoveMouseRelative(0,x) end local x=2 function OnEvent(event,arg) helper() end"));
        Reject(() => LuaMacroImport.Convert("function OnEvent(event,arg) for x=1,1000000 do for y=1,1000000 do end end PressAndReleaseKey('a') end"));
        Reject(() => LuaMacroImport.Convert("function OnEvent(event,arg) Sleep(" + new string('(', 256) + "1" + new string(')', 256) + ") end"));
        Reject(() => LuaMacroImport.Convert("function OnEvent(event,arg) local end=1 PressAndReleaseKey('a') end"));
    }

    [Test]
    public async Task LuaExecutesRealConditionalsHelpersAndSimultaneousChordWithRightButtonMapping()
    {
        var sink = new Sink(); await using var runtime = new LuaMacroRuntime(sink, key => key == "capslock");
        await runtime.StartAsync("""
            local enabled = true
            function click() PressAndReleaseMouseButton(3) end
            function OnEvent(event,arg)
              if event == "MOUSE_BUTTON_PRESSED" and arg == 3 and enabled and IsKeyLockOn("capslock") then
                click()
                PressAndReleaseKey("lctrl", "a")
              end
            end
            """);
        runtime.MouseButton(3, true);
        await sink.Wait(e => e.Kind == MacroInputKind.Key && e.Code == 224 && !e.Down);
        await runtime.StopAsync();
        var events = sink.Events.ToArray();
        Assert.Equal(6, events.Length);
        Assert.Equal(2, events[0].Code); Assert.Equal(MacroInputKind.MouseButton, events[0].Kind);
        Assert.Equal("224,4,4,224", string.Join(',', events.Skip(2).Select(e => e.Code)));
    }

    [Test]
    public async Task MouseReleaseStateUpdatesWhileLuaHandlerIsInsideLoopEvenWithoutReleaseEvent()
    {
        var sink = new Sink(); await using var runtime = new LuaMacroRuntime(sink);
        await runtime.StartAsync("""
            function OnEvent(event,arg)
              if event == "MOUSE_BUTTON_PRESSED" and arg == 1 then
                while IsMouseButtonPressed(1) do
                  MoveMouseRelative(0,1)
                  Sleep(2)
                end
                PressAndReleaseKey("b")
              end
            end
            """);
        runtime.MouseButton(1, true);
        await sink.Wait(e => e.Kind == MacroInputKind.Move);
        runtime.MouseButton(1, false, dispatchEvent: false);
        await sink.Wait(e => e.Kind == MacroInputKind.Key && e.Code == 5 && !e.Down);
        await runtime.StopAsync();
        Assert.Equal(false, runtime.IsRunning);
    }

    [Test]
    public async Task StoppingLuaInterruptsSleepAndReleasesEveryHeldInput()
    {
        var sink = new Sink(); await using var runtime = new LuaMacroRuntime(sink);
        await runtime.StartAsync("function OnEvent(event,arg) if event=='MOUSE_BUTTON_PRESSED' then PressKey('lctrl') PressMouseButton(1) Sleep(600000) end end");
        runtime.MouseButton(1, true);
        await sink.Wait(e => e.Kind == MacroInputKind.MouseButton && e.Down);
        var timer = Stopwatch.StartNew(); await runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(true, timer.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(2, sink.Events.Count(e => e.Down)); Assert.Equal(2, sink.Events.Count(e => !e.Down));
    }

    [Test]
    public async Task StopDuringProfileActivationAwaitsStartupAndPreventsDelayedInput()
    {
        var sink = new Sink(); await using var runtime = new LuaMacroRuntime(sink);
        var starting = runtime.StartAsync("""
            function OnEvent(event,arg)
              if event == "PROFILE_ACTIVATED" then
                PressKey("a")
                Sleep(600000)
                PressKey("b")
              end
            end
            """);
        await sink.Wait(e => e.Kind == MacroInputKind.Key && e.Code == 4 && e.Down);
        await runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        try { await starting; } catch (OperationCanceledException) { }
        Assert.Equal(true, starting.IsCompleted);
        Assert.Equal(2, sink.Events.Count);
        Assert.Equal(4, sink.Events.Last().Code); Assert.Equal(false, sink.Events.Last().Down);
        Assert.Equal(false, runtime.IsRunning);
        // A new independent session still starts after a canceled startup.
        await runtime.StartAsync("function OnEvent(event,arg) if event=='MOUSE_BUTTON_PRESSED' then PressAndReleaseKey('c') end end");
        runtime.MouseButton(1, true); await sink.Wait(e => e.Kind == MacroInputKind.Key && e.Code == 6 && !e.Down);
        await runtime.StopAsync();
        Assert.Equal(0, sink.Events.Count(e => e.Code == 5));
    }

    [Test]
    public async Task LuaSandboxHasNoHostAccessAndCpuLoopIsPreempted()
    {
        var sink = new Sink(); await using var runtime = new LuaMacroRuntime(sink);
        var logs = new ConcurrentQueue<string>(); runtime.Logged += (_, text) => logs.Enqueue(text);
        var fault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Faulted += (_, error) => fault.TrySetResult(error);
        await runtime.StartAsync("""
            assert(io == nil and os == nil and package == nil and debug == nil and require == nil and dofile == nil)
            function OnEvent(event,arg)
              if event == "PROFILE_ACTIVATED" then OutputLogMessage("sandbox %d", 1) end
              if event == "MOUSE_BUTTON_PRESSED" then PressKey("a") while true do end end
            end
            """);
        Assert.Equal(true, logs.Contains("sandbox 1"));
        runtime.MouseButton(1, true);
        var error = await fault.Task.WaitAsync(TimeSpan.FromSeconds(4));
        await runtime.StopAsync();
        Assert.Equal(true, error.Message.Contains("2,000,000"));
        Assert.Equal(1, sink.Events.Count(e => e.Down)); Assert.Equal(1, sink.Events.Count(e => !e.Down));
    }
    private static void Reject(Action action)
    { try { action(); } catch (Exception error) when (error is NotSupportedException or FormatException) { return; } throw new InvalidOperationException("Expected rejected Lua conversion."); }
}
