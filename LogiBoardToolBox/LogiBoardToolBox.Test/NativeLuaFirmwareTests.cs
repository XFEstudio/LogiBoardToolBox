using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class NativeLuaFirmwareTests
{
    internal const string Source = """
        EnablePrimaryMouseButtonEvents(true)
        function OnEvent(event, arg)
          if event == "MOUSE_BUTTON_PRESSED" and arg == 4 then
            Sleep(20)
            if IsMouseButtonPressed(4) then
              repeat
                PressMouseButton(1)
                Sleep(math.random(150,170))
                ReleaseMouseButton(1)
                Sleep(math.random(15,25))
              until not IsMouseButtonPressed(4)
            end
          end
        end
        """;

    internal static MemorySnapshot Extended()
    {
        var s = LegacyG502MacroTests.CapturedLayout();
        Convert.FromHexString("4C42540401").CopyTo(s.Description, 11); return s;
    }

    [Test]
    public void ExactUserScriptCompilesRuntimeRandomAndCurrentPhysicalStateWithoutChangingDelays()
    {
        var macro = LuaMacroImport.ConvertForFirmware(Source, 4, 3, Extended().Capabilities);
        Assert.Equal("074000140400080541000162009600AA42000162000F0019060008FF", Convert.ToHexString(macro.Bytes));
        Assert.Equal(MacroRepeat.Once, macro.Repeat); Assert.Equal(true, macro.UsesNativeLua);
        Assert.Equal(28, macro.Length);
        Assert.Equal(2, macro.Instructions.Count(i => i[0] == 0x62));
    }

    [Test]
    public void ExtensionDeclarationRequiresEveryMarkerAndGeometryByteAndKeepsToggleCapability()
    {
        var description = Extended().Description;
        Assert.Equal(true, Extended().Capabilities.SupportsNativeLua);
        Assert.Equal(true, Extended().Capabilities.SupportsNativeToggle);
        for (var i = 0; i < 16; i++)
        { var bad = description.ToArray(); bad[i] ^= 1; Assert.Equal(false, OnboardCapabilities.Parse(bad).SupportsNativeLua); }
        Assert.Equal(false, NativeToggleFirmwareTests.Extended().Capabilities.SupportsNativeLua);
        Assert.Equal(false, LegacyG502MacroTests.CapturedLayout().Capabilities.SupportsNativeLua);
    }

    [Test]
    public void StockAndOlderExtendedFirmwareRejectBothConversionAndAllocation()
    {
        var macro = LuaMacroImport.ConvertForFirmware(Source, 4, 3, Extended().Capabilities);
        foreach (var snapshot in new[] { LegacyG502MacroTests.CapturedLayout(), NativeToggleFirmwareTests.Extended() })
        {
            Reject(() => LuaMacroImport.ConvertForFirmware(Source, 4, 3, snapshot.Capabilities));
            Reject(() => MacroAllocator.Allocate(snapshot, [new(3, false, macro)]));
            Assert.Equal(false, snapshot.Capabilities.HasSameMacroExecution(Extended().Capabilities));
        }
        Reject(() => LuaMacroImport.Convert(Source, 4));
    }

    [Test]
    public void PhysicalBindingMaskIsEncodedRatherThanAssumingLuaArgumentIsTheFirmwareIndex()
    {
        for (var index = 0; index < 11; index++)
        {
            var macro = LuaMacroImport.ConvertForFirmware(Source, 4, index, Extended().Capabilities);
            foreach (var i in macro.Instructions.Where(i => i[0] is 4 or 6))
                Assert.Equal(1 << index, (i[1] << 8) | i[2]);
        }
    }

    [Test]
    public void AllocationReadbackAndBackupRestorePreserveNewOpcodesButCannotDowngradeExecutionSupport()
    {
        var s = Extended(); var macro = LuaMacroImport.ConvertForFirmware(Source, 4, 3, s.Capabilities);
        var plan = MemoryWritePlan.Create(s, s.GetProfiles()[0], [new(3, false, macro)]);
        foreach (var change in plan.Changes) s.Sectors[change.Sector] = change.After;
        var binding = s.GetProfiles()[0].GetBinding(3);
        var bytes = MacroAllocator.ReadMacroBytes(s, new(ButtonBinding.MacroSector(binding), ButtonBinding.MacroOffset(binding)));
        Assert.Equal(Convert.ToHexString(macro.Bytes), Convert.ToHexString(bytes));
        Assert.Equal(true, MacroAllocator.FindReferencedSectors(s).Contains(ButtonBinding.MacroSector(binding)));
        foreach (var old in new[] { LegacyG502MacroTests.CapturedLayout(), NativeToggleFirmwareTests.Extended() })
        {
            try { MemoryWritePlan.Restore(old, s); throw new Exception("Expected backup rejection"); }
            catch (InvalidDataException) { }
        }
    }

    [Test]
    public void DynamicConditionsCannotSilentlySkipFollowingActionsOrResampleStoredRandomValues()
    {
        var bodies = new[]
        {
            "if IsMouseButtonPressed(4) then PressAndReleaseMouseButton(1) end PressAndReleaseMouseButton(3)",
            "if IsMouseButtonPressed(4) then PressAndReleaseMouseButton(1) else PressAndReleaseMouseButton(3) end",
            "local delay = math.random(150,170) repeat Sleep(delay) until not IsMouseButtonPressed(4)",
            "Sleep(math.randomseed(1))",
            "if IsMouseButtonPressed(5) then PressAndReleaseMouseButton(1) end",
            "while IsMouseButtonPressed(4) do Sleep(10) end PressAndReleaseMouseButton(1)"
        };
        foreach (var body in bodies)
            Reject(() => LuaMacroImport.ConvertForFirmware("function OnEvent(event,arg) if event == 'MOUSE_BUTTON_PRESSED' and arg == 4 then " + body + " end end", 4, 3, Extended().Capabilities));
    }

    [Test]
    public void NativeInstructionsSurviveLegacyStorageContinuationAcrossSeveralSectors()
    {
        var source = Source.Replace("Sleep(20)", "for i=1,300 do Sleep(1) end Sleep(20)");
        var s = Extended(); var macro = LuaMacroImport.ConvertForFirmware(source, 4, 3, s.Capabilities);
        Assert.Equal(928, macro.Length);
        var plan = MemoryWritePlan.Create(s, s.GetProfiles()[0], [new(3, false, macro)]);
        foreach (var change in plan.Changes) s.Sectors[change.Sector] = change.After;
        var binding = s.GetProfiles()[0].GetBinding(3);
        var read = MacroAllocator.ReadMacroBytes(s, new(ButtonBinding.MacroSector(binding), ButtonBinding.MacroOffset(binding)));
        Assert.Equal(Convert.ToHexString(macro.Bytes), Convert.ToHexString(read));
        Assert.Equal(true, MacroAllocator.FindReferencedSectors(s).Count >= 4);
    }

    private static void Reject(Action action)
    { try { action(); } catch (NotSupportedException) { return; } throw new Exception("Expected unsupported conversion rejection"); }
}
