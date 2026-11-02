using System.Text.Json;
using LogiBoardToolBox.Controls;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.ViewModels;
using LogiBoardToolBox.Views;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace LogiBoardToolBox.Utilities;

internal static partial class UiVerification
{
    private static async Task VerifyToggleAsync(MainPage page, WorkspaceHost tabs, Func<string, Task> capture, Action<string, bool, string> check)
    {
        var at = Array.IndexOf(args, "--toggle-snapshot");
        if (at < 0) return;
        if (at + 1 >= args.Length) throw new ArgumentException("需要触发宏诊断快照路径。");
        var snapshot = await OnboardMemory.LoadBackupAsync(args[at + 1]);
        var vm = page.ViewModel; vm.LoadToggleDiagnosticSnapshot(snapshot); tabs.SelectedIndex = 0;
        var replay = vm.AttachDiagnosticReplay();
        try
        {
            check("toggle-captured-duplicate-payload", vm.Macros.Count == 2 && vm.Macros[0].Script == vm.Macros[1].Script && vm.StandardBindings[7].SelectedAction.Id == "keep", "The two identical user macros must not be guessed by bytecode alone.");
            vm.MacroTarget = vm.StandardBindings[7]; vm.BindMacroCommand.Execute(null); await vm.FlushImmediateSaveAsync();
            check("toggle-link-initial-save", !vm.HasError && replay.WriteStarts == 0, "The native macro already matches; persist its name without rewriting flash. " + vm.Status);
            await vm.ReloadMacroLinksForVerificationAsync();
            check("toggle-links-survive-reload-with-duplicate-bytecode", vm.StandardBindings[7].SelectedAction.Id == "macro" && vm.StandardBindings[7].Value == "左键速点", "Per-device/profile/button metadata restores the exact name from disk even when another macro has identical instructions.");
            var writes = replay.WriteStarts;
            var before = replay.Sectors.ToDictionary(s => s.Key, s => Convert.ToHexString(s.Value));
            var macro = vm.SelectedMacro!;
            var libraryPath = Path.Combine(SettingsDirectory, "macros.json");
            await vm.SaveMacrosCommand.ExecuteAsync(null); await vm.FlushImmediateSaveAsync();
            var savedLibrary = await File.ReadAllTextAsync(libraryPath);
            tabs.SelectedIndex = 1; await Task.Delay(60);
            var toggleButton = Tree(page).OfType<Button>().Single(b => b.Name == "ToggleType");
            ((IInvokeProvider)new ButtonAutomationPeer(toggleButton).GetPattern(PatternInterface.Invoke)).Invoke();
            await Until(() => macro.Repeat == MacroRepeat.Toggle, "choose Toggle");
            check("toggle-real-type-button-and-hint", vm.ShowSoftwareToggleHint && macro.SelectedRepeat.Id == "2" && vm.MacroTypeDescription.Contains("同键触发"), "The actual third type button chooses Toggle and displays the board limitation.");
            await capture("G502Hero-toggle-compatibility");
            Invoke(page, "macro-save"); await Until(() => vm.HasError && !vm.IsBusy, "reject unsafe board loop");
            check("toggle-save-does-not-change-library-or-flash", await File.ReadAllTextAsync(libraryPath) == savedLibrary && replay.WriteStarts == writes && before.All(s => s.Value == Convert.ToHexString(replay.Sectors[s.Key])), "Unsafe conversion is rejected before saving either macros.json or the board; no fallback to WhileHeld.");
            await capture("G502Hero-toggle-save-explanation");
            var untouched = vm.StandardBindings[3]; vm.SelectedButton = untouched;
            var untouchedAction = untouched.SelectedAction; var untouchedValue = untouched.Value;
            vm.AssignPaletteItemCommand.Execute(new AssignmentItem(macro.Name, "macros", "macro", macro.Name, "触发"));
            check("toggle-palette-does-not-stage-unsafe-binding", untouched.SelectedAction == untouchedAction && untouched.Value == untouchedValue && replay.WriteStarts == writes, "The map/drag assignment path validates Toggle before mutating the target or requesting an auto-save.");
            page.CancelNewMacro();
            check("toggle-cancel-restores-previous-repeat", macro.Repeat == MacroRepeat.WhileHeld && !vm.ShowSoftwareToggleHint, "Cancel restores the saved WhileHeld type rather than recording a false successful Toggle save.");
            vm.HasError = false;
            tabs.SelectedIndex = 1;
            macro.SelectedRepeat = MacroEditor.RepeatChoices[2];
            await vm.UseSoftwareMacroConfigurationCommand.ExecuteAsync(null);
            check("toggle-explicit-software-switch-preserves-board", vm.IsEditingSoftware && vm.StandardBindings[7].SelectedAction.Id == "macro" && vm.StandardBindings[7].Value == macro.Name && vm.StandardBindings[3].SelectedAction.Id != "macro" && replay.WriteStarts == writes, "User-invoked switch keeps G8 as the target despite another selected map button, uses RAM host mode, and performs no flash write.");
            await vm.SaveMacrosCommand.ExecuteAsync(null);
            using var library = JsonDocument.Parse(await File.ReadAllTextAsync(libraryPath));
            check("toggle-software-repeat-persists", library.RootElement.EnumerateArray().Single(m => m.GetProperty("Name").GetString() == macro.Name).GetProperty("Repeat").GetInt32() == 2 && !vm.HasError, "Software Toggle is persisted as Toggle.");
            await vm.TestMacroCommand.ExecuteAsync(null);
            check("toggle-software-simulation-stops", !vm.HasError && vm.MacroExecutionTrace.Contains("MouseButton 1 ↓") && vm.MacroExecutionTrace.Contains("MouseButton 1 ↑") && !vm.MacroExecutionTrace.Contains("超过"), "Trace sink only: real runtime starts and stops on the second press during a 200ms recorded delay.");
            await capture("G502Hero-software-toggle");
            check("toggle-other-profile-bytes-unchanged", before.Where(s => s.Key is >= 2 and <= 5).All(s => s.Value == Convert.ToHexString(replay.Sectors[s.Key])), "Every other profile remains unchanged.");
            page.CancelNewMacro();
        }
        finally { vm.DetachDiagnosticReplay(); }
        // Protocol-extension simulation only. This does not install or verify
        // modified firmware on a physical mouse.
        var extended = JsonSerializer.Deserialize<MemorySnapshot>(JsonSerializer.Serialize(snapshot))!;
        extended.Description = extended.Description.Take(11).Concat(Convert.FromHexString("4C42540302")).ToArray();
        vm.LoadToggleDiagnosticSnapshot(extended); tabs.SelectedIndex = 1;
        var nativeReplay = vm.AttachDiagnosticReplay();
        try
        {
            await Task.Delay(60);
            var toggleButton = Tree(page).OfType<Button>().Single(b => b.Name == "ToggleType");
            ((IInvokeProvider)new ButtonAutomationPeer(toggleButton).GetPattern(PatternInterface.Invoke)).Invoke();
            await Until(() => vm.SelectedMacro?.Repeat == MacroRepeat.Toggle, "choose native Toggle");
            check("native-toggle-capability-drives-editor", !vm.ShowSoftwareToggleHint && vm.ShowNativeToggleHint && vm.MacroTypeDescription.Contains("关闭软件后仍有效"), "Exact extension marker and matching geometry permit the real Toggle type without a software fallback.");
            vm.MacroTarget = vm.StandardBindings[7];
            vm.BindMacroCommand.Execute(null); await vm.SaveMacrosCommand.ExecuteAsync(null); await vm.FlushImmediateSaveAsync();
            await Until(() => vm.IsIdle && Tree(page).OfType<MacroEditorView>().All(e => e.IsEnabled), "native editor reenabled after save");
            check("native-toggle-auto-save-preserves-mode", !vm.HasError && !vm.IsEditingSoftware && !vm.HostMacrosRunning && nativeReplay.WriteStarts > 0, vm.Status);
            check("native-toggle-editor-usable-after-save", vm.IsIdle && Tree(page).OfType<MacroEditorView>().All(e => e.IsEnabled), "The real editor returns from its write transaction; control availability is checked before capture.");
            await capture("G502Hero-native-toggle-editor");
            await vm.ReloadMacroLinksForVerificationAsync();
            var binding = vm.StandardBindings[7].Original;
            check("native-toggle-readback-and-name", ButtonBinding.IsNativeToggle(binding) && vm.StandardBindings[7].SelectedAction.Id == "macro" && vm.StandardBindings[7].Value == "左键速点", "Native action 3 survives readback and exact per-binding name association.");
            // Use the replay's actual committed pages, not the pre-save baseline.
            var encoded = new MemorySnapshot { Description = extended.Description, Sectors = nativeReplay.Sectors.ToDictionary(p => p.Key, p => p.Value) };
            var bytes = MacroAllocator.ReadMacroBytes(encoded, new(ButtonBinding.MacroSector(binding), ButtonBinding.MacroOffset(binding)));
            check("native-toggle-recorded-delays", Convert.ToHexString(bytes) == "4100014000C84200014000C803FF", Convert.ToHexString(bytes));
            await File.WriteAllTextAsync(Path.Combine(OutputDirectory, "native-encoded-board.json"), JsonSerializer.Serialize(new
            {
                HardwareAccess = false, SimulatedFirmwareCapability = true, NativePatchInstalled = false,
                Description = Convert.ToHexString(extended.Description), Profile = vm.SelectedProfile!.Sector, Button = 7,
                Sectors = nativeReplay.Sectors.ToDictionary(p => p.Key, p => Convert.ToHexString(p.Value))
            }, new JsonSerializerOptions { WriteIndented = true }));
            tabs.SelectedIndex = 0; await capture("G502Hero-native-toggle-assignment");
        }
        finally { vm.DetachDiagnosticReplay(); }
    }
}
