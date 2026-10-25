using System.Text.Json.Nodes;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class GHubImportTests
{
    [Test]
    public void RecordedMouseDelaysOverrideEditorDefaultMetadata()
    {
        var root = Example();
        var import = GHubMacroImport.Read(root.ToJsonString());
        var macro = import.Bindings.Single();
        Assert.Equal("10,10", string.Join(',', macro.RecordedDelays));
        Assert.Equal("41000140000A42000140000A02FF", Convert.ToHexString(macro.Compiled.Bytes));
        Assert.Equal(MacroRepeat.WhileHeld, macro.Repeat);
        Assert.Equal(5, macro.ButtonIndex); // G HUB G6 is the sixth physical binding record.
    }

    [Test]
    public async Task G5AndG6StayOnTheirOwnPhysicalRecordsWhenImportedTogether()
    {
        var root = Example(); var cards = root["cards"]!["cards"]!.AsArray();
        var cancel = cards[0]!.DeepClone(); cancel["id"] = "cancel"; cancel["name"] = "Cancel";
        cancel["macro"]!["sequence"]!["heldSequence"]!["components"] = JsonNode.Parse("""
            [{"keyboard":{"hidUsage":"49","isDown":true}}, {"delay":{"durationMs":59}},
             {"keyboard":{"hidUsage":"49"}}, {"delay":{"durationMs":198}},
             {"keyboard":{"hidUsage":"30","isDown":true}}, {"delay":{"durationMs":25}},
             {"keyboard":{"hidUsage":"30"}}, {"delay":{"durationMs":10}}]
            """);
        cards.Add(cancel);
        root["profiles"]!["profiles"]![0]!["assignments"]!.AsArray().Add(JsonNode.Parse("""{"slotId":"g502x-plus_g5_m1","cardId":"cancel"}"""));
        var import = GHubMacroImport.Read(root.ToJsonString()); var baseline = Fixture.Create(10);
        using var fake = new FakeMouse(baseline); using var client = new HidPlusPlusClient(fake, 1);
        var memory = new OnboardMemory(client, baseline.DeviceIdentity, baseline.DeviceName);
        var plan = MemoryWritePlan.Create(baseline, baseline.GetProfiles()[0].Copy(), import.Bindings.Select(b => new MacroAssignment(b.ButtonIndex, b.GShift, b.Compiled)).ToArray(), true);
        using var scratch = new ScratchDirectory(); await memory.CommitAsync(plan, scratch.Path);
        var after = await memory.ReadAsync(); var profile = after.GetProfiles()[0];
        byte[] Read(int index) { var b = profile.GetBinding(index); return MacroAllocator.ReadMacroBytes(after, new(ButtonBinding.MacroSector(b), ButtonBinding.MacroOffset(b))); }
        Assert.Equal("43003140003B4400314000C643001E40001944001E40000A02FF", Convert.ToHexString(Read(4)));
        Assert.Equal("41000140000A42000140000A02FF", Convert.ToHexString(Read(5)));
        for (var i = 0; i < 253; i++) if (i is < 48 or >= 56) Assert.Equal(baseline.Sectors[1][i], after.Sectors[1][i]);
    }

    [Test]
    public void RightButtonAndGShiftUseActualDeviceIndices()
    {
        var root = Example();
        Assignment(root)["slotId"] = "g502x-plus_g4_m1_shifted";
        foreach (var component in Sequence(root)["heldSequence"]!["components"]!.AsArray())
            if (component!["mouse"] is { } mouse) mouse["button"]!["hidUsage"] = "2";
        var macro = GHubMacroImport.Read(root.ToJsonString()).Bindings.Single();
        Assert.Equal(3, macro.ButtonIndex); Assert.Equal(true, macro.GShift);
        Assert.Equal("41000240000A42000240000A02FF", Convert.ToHexString(macro.Compiled.Bytes));
    }

    [Test]
    public void VariableKeyboardDelaysAndDecimalHidUsagesArePreserved()
    {
        var root = Example(); Assignment(root)["slotId"] = "g502x-plus_g5_m1";
        Sequence(root)["heldSequence"]!["components"] = JsonNode.Parse("""
            [{"keyboard":{"hidUsage":"49","displayName":"ignored","isDown":true}},
             {"delay":{"durationMs":59}}, {"keyboard":{"hidUsage":"49"}},
             {"delay":{"durationMs":198}}, {"keyboard":{"hidUsage":"30","isDown":true}},
             {"delay":{"durationMs":25}}, {"keyboard":{"hidUsage":"30"}}, {"delay":{"durationMs":10}}]
            """);
        var macro = GHubMacroImport.Read(root.ToJsonString()).Bindings.Single();
        Assert.Equal(4, macro.ButtonIndex);
        Assert.Equal("59,198,25,10", string.Join(',', macro.RecordedDelays));
        Assert.Equal("43003140003B4400314000C643001E40001944001E40000A02FF", Convert.ToHexString(macro.Compiled.Bytes));
    }

    [Test]
    public void SimpleMacroPreservesIndividualModifierTransitions()
    {
        var root = Example(); Assignment(root)["slotId"] = "g502x-plus_g10_m1";
        var sequence = Sequence(root); sequence.Remove("useRepeatActions"); sequence["useSimpleActions"] = true;
        sequence["heldSequence"] = new JsonObject();
        sequence["simpleSequence"] = JsonNode.Parse("""
            {"components":[{"keyboard":{"hidUsage":"29","isDown":true}}, {"delay":{"durationMs":10}},
             {"keyboard":{"hidUsage":"29"}}, {"delay":{"durationMs":20}},
             {"keyboard":{"hidUsage":"225","isDown":true}}, {"delay":{"durationMs":10}},
             {"keyboard":{"hidUsage":"225"}}, {"delay":{"durationMs":10}}]}
            """);
        var macro = GHubMacroImport.Read(root.ToJsonString()).Bindings.Single();
        Assert.Equal(9, macro.ButtonIndex); Assert.Equal(MacroRepeat.Once, macro.Repeat);
        // Captured from G HUB copying the same profile to the G502 X PLUS.
        Assert.Equal("43001D40000A44001D4000144302E140000A4402E140000AFF", Convert.ToHexString(macro.Compiled.Bytes));
    }

    [Test]
    public void LongAndZeroDelaysRetainTheirTotalDuration()
    {
        var root = Example();
        var components = Sequence(root)["heldSequence"]!["components"]!.AsArray();
        components[1]!["delay"]!["durationMs"] = 70000; components[3]!["delay"]!["durationMs"] = 0;
        var macro = GHubMacroImport.Read(root.ToJsonString()).Bindings.Single();
        Assert.Equal("70000,0", string.Join(',', macro.RecordedDelays));
        Assert.Equal("41000140FFFF40117142000102FF", Convert.ToHexString(macro.Compiled.Bytes));
    }

    [Test]
    public void UnknownOperationsAndIndependentHoldSequenceAreRejected()
    {
        var root = Example();
        Sequence(root)["heldSequence"]!["components"]!.AsArray().Add(JsonNode.Parse("""{"text":{"value":"unsupported"}}"""));
        Reject<NotSupportedException>(() => GHubMacroImport.Read(root.ToJsonString()));
        root = Example(); Sequence(root).Remove("useRepeatActions");
        Reject<NotSupportedException>(() => GHubMacroImport.Read(root.ToJsonString()));
    }

    [Test]
    public void AmbiguousActiveProfileAndDuplicateButtonAreRejected()
    {
        var root = Example(); var profiles = root["profiles"]!["profiles"]!.AsArray(); profiles.Add(profiles[0]!.DeepClone());
        Reject<InvalidDataException>(() => GHubMacroImport.Read(root.ToJsonString()));
        root = Example(); root["profiles"]!["profiles"]![0]!["assignments"]!.AsArray().Add(Assignment(root).DeepClone());
        Reject<InvalidDataException>(() => GHubMacroImport.Read(root.ToJsonString()));
    }

    [Test]
    public async Task ImportedMacroSurvivesCommitAndOtherProfileFieldsStayExact()
    {
        var import = GHubMacroImport.Read(Example().ToJsonString()); var before = Fixture.Create(10);
        using var fake = new FakeMouse(before); using var client = new HidPlusPlusClient(fake, 1);
        var memory = new OnboardMemory(client, before.DeviceIdentity, before.DeviceName);
        var profile = before.GetProfiles()[0].Copy();
        var plan = MemoryWritePlan.Create(before, profile, import.Bindings.Select(b => new MacroAssignment(b.ButtonIndex, b.GShift, b.Compiled)).ToArray(), true);
        using var scratch = new ScratchDirectory(); await memory.CommitAsync(plan, scratch.Path);
        var after = await memory.ReadAsync(); var binding = after.GetProfiles()[0].GetBinding(5);
        Assert.Equal(Convert.ToHexString(import.Bindings[0].Compiled.Bytes), Convert.ToHexString(MacroAllocator.ReadMacroBytes(after, new(ButtonBinding.MacroSector(binding), ButtonBinding.MacroOffset(binding)))));
        for (var i = 0; i < 253; i++) if (i is < 52 or >= 56) Assert.Equal(before.Sectors[1][i], after.Sectors[1][i]);
        foreach (var sector in before.Sectors.Keys.Where(s => s != 1 && s != 6)) Assert.Equal(Convert.ToHexString(before.Sectors[sector]), Convert.ToHexString(after.Sectors[sector]));
        Assert.Equal((byte)1, after.Mode); Assert.Equal((ushort)1, after.ActiveSector);
    }

    private static JsonObject Example() => JsonNode.Parse("""
        {"profiles/auto_switching/s_last_used_profile":"profile", "profiles":{"profiles":[
          {"id":"profile","name":"Default","activeForApplication":true,"assignments":[{"slotId":"g502x-plus_g6_m1","cardId":"macro"}]}]},
         "cards":{"cards":[{"id":"macro","name":"Recorded clicks","attribute":"MACRO_PLAYBACK","macro":{"type":"SEQUENCE",
          "sequence":{"defaultDelay":50,"useRepeatActions":true,"heldSequence":{"components":[
            {"mouse":{"button":{"hidUsage":"1","isDown":true}}},{"delay":{"durationMs":10}},
            {"mouse":{"button":{"hidUsage":"1"}}},{"delay":{"durationMs":10}}]}}}}]}}
        """)!.AsObject();
    private static JsonObject Sequence(JsonObject root) => root["cards"]!["cards"]![0]!["macro"]!["sequence"]!.AsObject();
    private static JsonObject Assignment(JsonObject root) => root["profiles"]!["profiles"]![0]!["assignments"]![0]!.AsObject();
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}.");
    }
}
