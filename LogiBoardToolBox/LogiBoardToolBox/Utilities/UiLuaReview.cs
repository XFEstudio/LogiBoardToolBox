using System.Text.Json;
using LogiBoardToolBox.Controls;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Views;

namespace LogiBoardToolBox.Utilities;

internal static partial class UiVerification
{
    private static async Task VerifyLuaWorkspaceAsync(MainPage page, WorkspaceHost tabs, Func<string, Task> capture, Action<string, bool, string> check)
    {
        var vm = page.ViewModel;
        vm.LuaName = "右键 Lua 验收";
        vm.LuaSource = "EnablePrimaryMouseButtonEvents(true)\nfunction OnEvent(event, arg)\n if event == \"MOUSE_BUTTON_PRESSED\" and arg == 3 then\n  PressMouseButton(3)\n  Sleep(59)\n  ReleaseMouseButton(3)\n  Sleep(198)\n  PressKey(\"1\")\n  Sleep(25)\n  ReleaseKey(\"1\")\n  Sleep(10)\n end\nend";
        vm.LuaExecuteLeft = false; vm.LuaExecuteRight = true;
        vm.LuaBoardTrigger = vm.LuaBoardTriggers[1]; vm.LuaBoardRepeat = vm.LuaBoardRepeats[2];
        page.OpenLuaEditor(); await capture("Lua-import-and-options");
        check("lua-export-control-visible", Tree(page).OfType<Microsoft.UI.Xaml.Controls.Button>().Any(b => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(b) == "lua-export" && b.IsEnabled && b.ActualWidth > 0), "The source editor exposes a real .lua export button; the system dialog is only opened by the user.");
        var sourceExportPath = Path.Combine(SettingsDirectory, "original-export.lua");
        Directory.CreateDirectory(SettingsDirectory);
        await LuaSourceFile.WriteAsync(sourceExportPath, vm.LuaSource);
        var originalExport = await LuaSourceFile.ReadAsync(sourceExportPath);
        check("lua-source-file-export-roundtrip", originalExport.Source == vm.LuaSource, "Exports and reimports the original Unicode source through the same file helper used by the Save dialog; no script executes.");
        Invoke(page, "lua-save");
        var path = Path.Combine(SettingsDirectory, "lua-profile.json");
        await Until(() => File.Exists(path) && !vm.IsBusy, "lua-save");
        using (var profile = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
            check("lua-ui-save-source-and-triggers", profile.RootElement.GetProperty("Source").GetString() == vm.LuaSource && !profile.RootElement.GetProperty("ExecuteLeft").GetBoolean() && profile.RootElement.GetProperty("ExecuteRight").GetBoolean(), "Real Lua Save button persists source and independent left/right event switches in the isolated settings directory.");
        var beforeToggle = vm.Macros.Count;
        Invoke(page, "lua-convert"); await Task.Delay(30); await Until(() => !vm.ConvertLuaToBoardCommand.IsRunning, "lua-convert");
        check("lua-toggle-board-conversion-not-misrepresented", vm.Macros.Count == beforeToggle && vm.LuaStatus.Contains("同一按键", StringComparison.Ordinal), "Lua Toggle cannot bypass the exact same-button cancellation check; no uninterruptible board macro is created.");
        await capture("Lua-toggle-not-written");
        vm.LuaBoardRepeat = vm.LuaBoardRepeats[1];
        Invoke(page, "lua-convert"); await Task.Delay(30); await Until(() => !vm.ConvertLuaToBoardCommand.IsRunning, "lua-convert");
        check("lua-ui-conversion-opens-macro", tabs.SelectedIndex == 1 && vm.SelectedMacro?.Name == "右键 Lua 验收" && vm.SelectedMacro.Repeat == MacroRepeat.WhileHeld, "Right-button event converted and the normal macro editor opened in WhileHeld mode.");
        var macro = vm.SelectedMacro!;
        var macroExportPath = Path.Combine(SettingsDirectory, "macro-export.lua");
        await LuaSourceFile.WriteAsync(macroExportPath, LuaMacroExport.Export(macro.Name, macro.CompileProgram(), 3));
        var exportedMacro = LuaMacroImport.Convert((await LuaSourceFile.ReadAsync(macroExportPath)).Source, 3);
        check("lua-macro-export-import-roundtrip", exportedMacro.CompileProgram().ToOnboard().Bytes.SequenceEqual(macro.Compile().Bytes), "Recorded macro -> runnable Lua file -> imported firmware instructions retains all delays, repeat mode and button numbering.");
        check("lua-conversion-keeps-delay-and-right-button", macro.Script.Contains("delay 59") && macro.Script.Contains("delay 198") && macro.Script.Contains("delay 25") && macro.Script.Contains("delay 10") && macro.Script.Contains("mouse 2 down") && !macro.UseStandardDelay, "G HUB button 3 maps to Windows/onboard right button 2; all four Sleep durations remain exact.");
        Invoke(page, "macro-save"); await Until(() => tabs.SelectedIndex == 0, "lua-converted-macro-save");
        check("lua-converted-macro-can-be-saved", File.ReadAllText(Path.Combine(SettingsDirectory,"macros.json")).Contains("右键 Lua 验收", StringComparison.Ordinal) || JsonDocument.Parse(File.ReadAllText(Path.Combine(SettingsDirectory,"macros.json"))).RootElement.EnumerateArray().Any(m=>m.GetProperty("Name").GetString()=="右键 Lua 验收"), "Save remains enabled for newly converted macros and persists the converted instructions.");
        await VerifyExpandedLuaAsync(page, tabs, capture, check);
        page.OpenLuaEditor();
        vm.LuaSource = "function OnEvent(event, arg) while true do MoveMouseRelative(0, 1) end end";
        var count = vm.Macros.Count; Invoke(page, "lua-convert"); await Task.Delay(30); await Until(() => !vm.ConvertLuaToBoardCommand.IsRunning, "lua-convert");
        check("lua-unsupported-board-conversion-is-explicit", vm.Macros.Count == count && vm.LuaStatus.StartsWith("无法转换为板载宏", StringComparison.Ordinal), vm.LuaStatus);
        await capture("Lua-board-limitation");
        check("lua-background-verification-does-not-run-global-input", !vm.LuaRunning, "Only conversion and isolated persistence are exercised; no Windows Lua hook or input sink started.");
        page.CloseLuaEditor();
        tabs.SelectedIndex = 1;
        vm.IsRecording = true;
        var recordedCount = vm.Timeline.Count;
        await capture("Recorder-internal-click-mode");
        Invoke(page, "macro-record-stop");
        check("recorder-stop-button-wiring", !vm.IsRecording && vm.Timeline.Count == recordedCount, "Actual stop control ends recording without creating a mouse step; no global hook or input injection is started in this isolated UI check.");
        check("recorder-internal-click-help", vm.RecordingStatus == "录制已停止", "During capture the recording help states app-internal clicks are included; native hook filtering is checked by deterministic RecorderTests.");
        tabs.SelectedIndex = 0;
    }

    private static async Task VerifyExpandedLuaAsync(MainPage page, WorkspaceHost tabs, Func<string, Task> capture, Action<string, bool, string> check)
    {
        var vm = page.ViewModel;
        page.OpenLuaEditor(); vm.LuaName = "Lua 表格轨迹";
        vm.LuaBoardTrigger = vm.LuaBoardTriggers[1]; vm.LuaBoardRepeat = vm.LuaBoardRepeats[0];
        vm.LuaSource = """
            local pattern = {{x=1.9, y=3, delay=59}, {x=-2, y=4, delay=198}}
            local function adjust(value) return math.floor(value) end
            function OnEvent(event, arg)
                if event == "MOUSE_BUTTON_PRESSED" and arg == 3 then
                    for _, step in ipairs(pattern) do
                        MoveMouseRelative(adjust(step.x), step.y)
                        Sleep(step.delay)
                    end
                end
            end
            """;
        await capture("Lua-table-source"); Invoke(page, "lua-convert"); await Task.Delay(30); await Until(() => !vm.ConvertLuaToBoardCommand.IsRunning, "lua-convert");
        check("lua-table-functions-and-math-ui", tabs.SelectedIndex == 1 && vm.SelectedMacro?.Name == "Lua 表格轨迹" && vm.SelectedMacro.Script.Contains("move 1 3") && vm.SelectedMacro.Script.Contains("move -2 4") && vm.SelectedMacro.Script.Contains("delay 198"), vm.LuaStatus);
        await capture("Lua-table-converted");
        Invoke(page, "macro-save"); await Until(() => tabs.SelectedIndex == 0, "expanded-lua-save");
        page.OpenLuaEditor(); vm.LuaName = "Lua 按下松开配对";
        vm.LuaBoardTrigger = vm.LuaBoardTriggers[0]; vm.LuaBoardRepeat = vm.LuaBoardRepeats[0];
        vm.LuaSource = """
            function OnEvent(event, arg)
                if arg ~= 1 then return end
                if event == "MOUSE_BUTTON_PRESSED" then
                    PressKey("lctrl")
                    PressMouseButton(1)
                elseif event == "MOUSE_BUTTON_RELEASED" then
                    Sleep(25)
                    ReleaseMouseButton(1)
                    ReleaseKey("lctrl")
                end
            end
            """;
        Invoke(page, "lua-convert"); await Task.Delay(30); await Until(() => !vm.ConvertLuaToBoardCommand.IsRunning, "lua-convert");
        var sequence = vm.SelectedMacro!;
        check("lua-release-events-preserved-ui", tabs.SelectedIndex == 1 && sequence.Name == vm.LuaName && sequence.Repeat == MacroRepeat.Sequence && sequence.ReleaseScript.Contains("delay 25") && sequence.ReleaseScript.Contains("up"), vm.LuaStatus);
        check("lua-sequence-compiles-wait-release", sequence.Compile().Instructions.Count(b => b.Length == 1 && b[0] == 1) == 1, "Paired Lua press/release produces a firmware wait-release barrier and balanced release instructions.");
        var sequenceLua = LuaMacroExport.Export(sequence.Name, sequence.CompileProgram(), 1);
        var restoredSequence = LuaMacroImport.Convert(sequenceLua, 1);
        check("lua-export-sequence-dsl-remains-editable", MacroCompiler.CompileProgram(restoredSequence.Script, restoredSequence.Repeat, restoredSequence.HoldScript, restoredSequence.ReleaseScript).ToOnboard().Bytes.SequenceEqual(sequence.Compile().Bytes), "Exported Ctrl + mouse press/release restores editable DSL without duplicate modifier edges, not only raw hidden metadata.");
        await capture("Lua-press-release-converted");
        Invoke(page, "macro-save"); await Until(() => tabs.SelectedIndex == 0, "lua-sequence-save");
        using (var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(SettingsDirectory, "macros.json"))))
            check("lua-release-phase-persists", saved.RootElement.EnumerateArray().Any(m => m.GetProperty("Name").GetString() == sequence.Name && m.GetProperty("Repeat").GetInt32() == 3 && m.GetProperty("ReleaseScript").GetString() == sequence.ReleaseScript), "The real macro Save button persists the distinct release phase and sequence mode.");
        page.OpenLuaEditor(); vm.LuaName = "Lua 仅松开";
        vm.LuaSource = "function OnEvent(event,arg) if event == 'MOUSE_BUTTON_RELEASED' and arg == 1 then PressAndReleaseKey('1') end end";
        Invoke(page, "lua-convert"); await Task.Delay(30); await Until(() => !vm.ConvertLuaToBoardCommand.IsRunning, "lua-convert");
        check("lua-release-only-ui", tabs.SelectedIndex == 1 && vm.SelectedMacro?.Name == vm.LuaName && vm.SelectedMacro.Repeat == MacroRepeat.Sequence && string.IsNullOrWhiteSpace(vm.SelectedMacro.Script) && !string.IsNullOrWhiteSpace(vm.SelectedMacro.ReleaseScript), vm.LuaStatus);
        await capture("Lua-release-only-converted");
        Invoke(page, "macro-save"); await Until(() => tabs.SelectedIndex == 0, "lua-release-only-save");
    }
}
