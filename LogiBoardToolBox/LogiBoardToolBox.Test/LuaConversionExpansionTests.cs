using LogiBoardToolBox.Core.Macros;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class LuaConversionExpansionTests
{
    [Test]
    public void IndexedRecoilTablesMathAndHelperReturnsExpandWithRecordedDelays()
    {
        var conversion = LuaMacroImport.Convert("""
            local pattern = {{x=1.9,y=3,delay=59},{x=-2.1,y=4,delay=198}}
            local function adjust(v) return math.floor(v) end
            function OnEvent(event,arg)
              if event ~= "MOUSE_BUTTON_PRESSED" or arg ~= 1 then return end
              for _,step in ipairs(pattern) do
                MoveMouseRelative(adjust(step.x), math.max(step.y, 2))
                Sleep(step.delay)
              end
            end
            """);
        Assert.Equal("move 1 3\ndelay 59\nmove -3 4\ndelay 198", conversion.Script);
        Assert.Equal(MacroRepeat.Once, conversion.Repeat);
        Assert.Equal(true, conversion.CompileProgram().ToOnboard().Length > 0);
    }

    [Test]
    public void MultipleReturnsGroupedCallsAndFinalTableCallFollowLuaExpansionRules()
    {
        var conversion = LuaMacroImport.Convert("""
            local function pair() return 3, 4 end
            function OnEvent(event,arg)
              if event ~= "MOUSE_BUTTON_PRESSED" then return end
              local x,y = pair()
              MoveMouseRelative(x,y)
              local a,b = (pair())
              MoveMouseRelative(a, b or 9)
              local points = {1, pair()}
              MoveMouseRelative(#points,points[3])
            end
            """);
        Assert.Equal("move 3 4\nmove 3 9\nmove 3 4", conversion.Script);
    }

    [Test]
    public void MutableLocalTablesAndPureFactoryReturnsDoNotBecomePersistentAliases()
    {
        var conversion = LuaMacroImport.Convert("""
            local function create() return {2,4} end
            function OnEvent(event,arg)
              if event ~= "MOUSE_BUTTON_PRESSED" then return end
              local values = create()
              table.insert(values,2,3)
              local removed = table.remove(values,1)
              values[2] = values[2] + removed
              for _,v in pairs(values) do MoveMouseRelative(0,v) end
              local name = table.concat({"f", tostring(#values)}, "")
              PressAndReleaseKey(name)
            end
            """);
        Assert.Equal("move 0 3\nmove 0 6\nkey F2 down\nkey F2 up", conversion.Script);
    }

    [Test]
    public void FiniteWhileRepeatBreakAndCapturedLocalMutationTerminateWithoutHostState()
    {
        var conversion = LuaMacroImport.Convert("""
            function OnEvent(event,arg)
              if event ~= "MOUSE_BUTTON_PRESSED" then return end
              local count=0
              local function next() count=count+1 return count end
              while count < 3 do MoveMouseRelative(0,next()) end
              repeat
                local nextValue = next()
                Sleep(nextValue)
              until count >= 5
              while true do
                count=count-1
                if count == 2 then break end
                MoveMouseRelative(0,count)
              end
            end
            """);
        Assert.Equal("move 0 1\nmove 0 2\nmove 0 3\ndelay 4\ndelay 5\nmove 0 4\nmove 0 3", conversion.Script);
    }

    [Test]
    public void SimultaneousAssignmentEvaluatesAllTargetsBeforeAnyWrite()
    {
        var conversion = LuaMacroImport.Convert("""
            function OnEvent(event,arg)
              if event ~= "MOUSE_BUTTON_PRESSED" then return end
              local i=1
              local t={10,20}
              i,t[i]=2,30
              MoveMouseRelative(t[1],i)
              i,t[2]=t[2],i
              MoveMouseRelative(i,t[2])
            end
            """);
        Assert.Equal("move 30 2\nmove 20 2", conversion.Script);
    }

    [Test]
    public void LaterGlobalHelperIsVisibleButLaterLocalDoesNotLeakIntoEarlierClosure()
    {
        var conversion = LuaMacroImport.Convert("""
            function OnEvent(event,arg)
              if event ~= "MOUSE_BUTTON_PRESSED" then return end
              later()
              if hidden then MoveMouseRelative(9,9) end
            end
            function later() MoveMouseRelative(1,2) end
            local hidden = true
            """);
        Assert.Equal("move 1 2", conversion.Script);
    }

    [Test]
    public void ReleaseOnlyAndHeldPressReleaseUseWaitReleaseFirmwareSequence()
    {
        var paired = LuaMacroImport.Convert("""
            function OnEvent(event,arg)
              if arg ~= 3 then return end
              if event == "MOUSE_BUTTON_PRESSED" then PressKey("lctrl") PressMouseButton(3) end
              if event == "MOUSE_BUTTON_RELEASED" then Sleep(25) ReleaseMouseButton(3) ReleaseKey("lctrl") end
            end
            """, 3);
        Assert.Equal(MacroRepeat.Sequence, paired.Repeat);
        Assert.Equal("key Ctrl down\nmouse 2 down", paired.Script);
        Assert.Equal("delay 25\nmouse 2 up\nkey Ctrl up", paired.ReleaseScript);
        Assert.Equal(true, paired.CompileProgram().ToOnboard().Instructions.Any(i => i[0] == 1));
        var releaseOnly = LuaMacroImport.Convert("function OnEvent(event,arg) if event=='MOUSE_BUTTON_RELEASED' then PressAndReleaseKey('a') end end");
        Assert.Equal("", releaseOnly.Script); Assert.Equal(MacroRepeat.Sequence, releaseOnly.Repeat);
        Assert.Equal("01430004440004FF", Convert.ToHexString(releaseOnly.CompileProgram().ToOnboard().Bytes));
        Reject(() => LuaMacroImport.Convert("function OnEvent(event,arg) if event=='MOUSE_BUTTON_RELEASED' then PressAndReleaseKey('a') end end", 1, MacroRepeat.Toggle));
    }

    [Test]
    public void OtherMouseEventsCannotMutateConstantsThroughAliasesHelpersOrIterators()
    {
        foreach (var mutation in new[]
        {
            "local alias=cfg alias[1].x=9",
            "for _,entry in ipairs(cfg) do entry.x=9 end",
            "local function mutate(t) t[1].x=9 end mutate(cfg)",
            "local function get() return cfg end local alias=get() alias[1].x=9",
            "local function get(flag) local result={} if flag then result=cfg end return result end local alias=get(true) alias[1].x=9",
            "table.insert(cfg,{x=9})",
        })
        {
            var source = "local cfg={{x=1}} function OnEvent(event,arg) if arg==5 then " + mutation + " end if arg==1 and event=='MOUSE_BUTTON_PRESSED' then MoveMouseRelative(0,cfg[1].x) end end";
            Reject(() => LuaMacroImport.Convert(source, 1));
        }
        Reject(() => LuaMacroImport.Convert("local enabled=false function OnEvent(e,a) if a==5 then enabled=not enabled end if enabled and a==1 then PressAndReleaseKey('a') end end"));
        Reject(() => LuaMacroImport.Convert("function OnEvent(e,a) if e=='PROFILE_ACTIVATED' then EnablePrimaryMouseButtonEvents(false) end if a==1 then PressAndReleaseKey('a') end end"));
    }

    [Test]
    public void QuotasNondeterminismAndUndefinedArrayOrderFailWithoutPartialProgram()
    {
        Reject(() => LuaMacroImport.Convert("function OnEvent(e,a) if e=='MOUSE_BUTTON_PRESSED' then local n=0 while true do n=n+1 end end end"));
        Reject(() => LuaMacroImport.Convert("local t={a=1,b=2} function OnEvent(e,a) for k,v in pairs(t) do MoveMouseRelative(0,v) end end"));
        Reject(() => LuaMacroImport.Convert("function OnEvent(e,a) MoveMouseRelative(math.random(1,2),1) end"));
        Reject(() => LuaMacroImport.Convert("function OnEvent(e,a) local a={1,nil,3} MoveMouseRelative(#a,1) end"));
        Reject(() => LuaMacroImport.Convert("function OnEvent(e,a) PressAndReleaseKey(true .. '1') end"));
    }

    [Test]
    public void HoldIterationCanUseFreshLocalCountersButCannotChangeOuterState()
    {
        var conversion = LuaMacroImport.Convert("""
            function OnEvent(event,arg)
              if event == "MOUSE_BUTTON_PRESSED" and arg == 1 then
                while IsMouseButtonPressed(1) do
                  local n=0
                  while n<2 do n=n+1 MoveMouseRelative(0,n) Sleep(10) end
                end
              end
            end
            """);
        Assert.Equal(MacroRepeat.WhileHeld, conversion.Repeat);
        Assert.Equal("move 0 1\ndelay 10\nmove 0 2\ndelay 10", conversion.Script);
        Reject(() => LuaMacroImport.Convert("function OnEvent(e,a) local n=0 while IsMouseButtonPressed(1) do n=n+1 MoveMouseRelative(0,n) end end"));
    }

    private static void Reject(Action action)
    { try { action(); } catch (Exception error) when (error is NotSupportedException or FormatException) { return; } throw new InvalidOperationException("Expected unsupported Lua to be rejected."); }
}
