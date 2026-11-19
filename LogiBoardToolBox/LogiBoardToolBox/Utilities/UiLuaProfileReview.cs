using System.Diagnostics;
using LogiBoardToolBox.Controls;
using LogiBoardToolBox.Core.Devices;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.ViewModels;
using LogiBoardToolBox.Views;

namespace LogiBoardToolBox.Utilities;

internal static partial class UiVerification
{
    private static async Task VerifyLuaProfileUpdateAsync(MainPage page, WorkspaceHost tabs, Func<string, Task> capture, Action<string, bool, string> check)
    {
        var vm = page.ViewModel;
        vm.LoadCatalogVerificationFixture(MouseCatalog.ById("GPW")!);
        var replay = vm.AttachDiagnosticReplay();
        try
        {
            var original = replay.Sectors.ToDictionary(p => p.Key, p => Convert.ToHexString(p.Value));
            tabs.SelectedIndex = 4;
            await Task.Delay(100); page.UpdateLayout();
            var expander = Tree(page).OfType<Expander>().Single(e => e.Header as string == "侧键直达板载配置");
            expander.IsExpanded = true;
            vm.PairFirstProfile = vm.PairProfileChoices.Single(p => p.Id == "1");
            vm.PairSecondProfile = vm.PairProfileChoices.Single(p => p.Id == "4");
            expander.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false });
            await capture("Profile-pair-before-write");
            Invoke(page, "profile-pair-apply"); await Task.Delay(50); await Until(() => !vm.ApplyProfilePairCommand.IsRunning, "pair-write");
            check("profile-pair-real-command-readback", !vm.HasError && vm.ProfilePairActive && vm.Profiles.Count(p => p.Enabled) == 2 && replay.WriteStarts > 0, vm.Status);
            check("profile-pair-display-labels", vm.StandardBindings.Count(b => b.FriendlyName.StartsWith("直达板载配置 ")) == 2, vm.PairStatus);
            check("profile-pair-backup-and-local-restore-record", File.Exists(vm.LastBackupPath) && File.Exists(Path.Combine(SettingsDirectory, "profile-switch-links.json")), vm.LastBackupPath);
            expander.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false });
            await capture("Profile-pair-written");
            vm.SelectedButton = vm.StandardBindings[int.Parse(vm.PairFirstButton!.Id)];
            var writes = replay.WriteStarts; vm.DisableSelectedButtonCommand.Execute(null);
            await vm.FlushImmediateSaveAsync();
            check("profile-pair-reserved-key-cannot-break-return-path", replay.WriteStarts == writes && vm.ProfilePairActive && vm.HasError, vm.Status);
            vm.BindMacroCommand.Execute(null); await vm.FlushImmediateSaveAsync();
            check("profile-pair-macro-bind-cannot-overwrite-reserved-key", replay.WriteStarts == writes && vm.ProfilePairActive && vm.HasError, vm.Status);
            vm.HasError = false;
            await vm.SelectConfigurationAsync(vm.Configurations.Single(c => c.Onboard && c.Sector == 4));
            check("profile-pair-select-second-retains-destination-labels", vm.SelectedProfile?.Sector == 4 && vm.StandardBindings.Count(b => b.FriendlyName.StartsWith("直达板载配置 ")) == 2, vm.Status);
            Invoke(page, "profile-pair-remove"); await Task.Delay(50); await Until(() => !vm.RemoveProfilePairCommand.IsRunning, "pair-remove");
            check("profile-pair-remove-restores-every-sector", !vm.HasError && !vm.ProfilePairActive && original.All(p => replay.Sectors.TryGetValue(p.Key, out var data) && p.Value == Convert.ToHexString(data)), vm.Status);
            expander.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false });
            await capture("Profile-pair-restored");
        }
        finally { vm.DetachDiagnosticReplay(); }
        vm.LoadCatalogVerificationFixture(MouseCatalog.ById("GPW")!);
        await VerifyLuaWorkspaceAsync(page, tabs, capture, check);
        await VerifyContinuousMacroAsync(page, capture, check);
        page.OpenLuaEditor(); vm.LuaName = "大文件保留录制延迟";
        var largePath = Path.Combine(SettingsDirectory, "large-import.lua");
        var source = "--[[" + new string(' ', 1_200_000) + "]]\n" + MainPageViewModel.ExampleLua;
        await LuaSourceFile.WriteAsync(largePath, source);
        vm.LuaSource = (await LuaSourceFile.ReadAsync(largePath)).Source;
        check("large-source-editor-bounded", vm.LuaEditorText.Length <= LuaEditorDocument.PageCharacters + 1 && vm.HasNextLuaPage && vm.LuaSource == source, vm.LuaPageStatus);
        vm.NextLuaPageCommand.Execute(null); vm.LuaEditorText = " " + vm.LuaEditorText;
        await vm.SaveLuaCommand.ExecuteAsync(null);
        check("large-source-paged-edit-save-preserves-unseen-source", vm.LuaSource == source.Insert(LuaEditorDocument.PageCharacters, " "), vm.LuaPageStatus);
        while (vm.HasNextLuaPage) vm.NextLuaPageCommand.Execute(null);
        check("large-source-last-page-displays-original-event", vm.LuaEditorText.Contains("function OnEvent") && vm.LuaEditorText.Contains("Sleep(100)"), vm.LuaPageStatus);
        await capture("Lua-large-source-paged");
        vm.LuaBoardTrigger = vm.LuaBoardTriggers[0]; vm.LuaBoardRepeat = vm.LuaBoardRepeats[0];
        await vm.ConvertLuaToBoardCommand.ExecuteAsync(null);
        check("large-source-converts-small-program", vm.SelectedMacro?.Name == vm.LuaName && vm.SelectedMacro.Script.Contains("delay 50") && vm.SelectedMacro.Script.Contains("delay 100"), vm.LuaStatus);
        page.OpenLuaEditor();
        vm.LuaSource = "function OnEvent(event,arg) if event=='MOUSE_BUTTON_PRESSED' then local n=0 for i=1,1000000000 do n=n+1 end PressAndReleaseKey('a') end end";
        var count = vm.Macros.Count; var watch = Stopwatch.StartNew();
        var converting = vm.ConvertLuaToBoardCommand.ExecuteAsync(null);
        await Task.Delay(20);
        check("lua-conversion-ui-yields-and-cancel-enabled", !converting.IsCompleted && vm.LuaConverting && vm.ConvertLuaToBoardCancelCommand.CanExecute(null), "Dispatcher resumed while compiler was evaluating a long finite loop.");
        vm.ConvertLuaToBoardCancelCommand.Execute(null); await converting;
        check("lua-conversion-cancel-does-not-create-partial-macro", vm.Macros.Count == count && !vm.LuaConverting && vm.LuaStatus.Contains("已取消") && watch.Elapsed < TimeSpan.FromSeconds(5), vm.LuaStatus);
        await capture("Lua-conversion-cancelled");
        page.CloseLuaEditor();
        var macro = vm.SelectedMacro!; macro.Script = string.Join('\n', Enumerable.Repeat("tap A\ndelay 59", 4000));
        tabs.SelectedIndex = 1; page.UpdateLayout();
        var phase = Tree(page).OfType<MacroPhaseView>().Single(p => p.Segment == "press");
        check("long-macro-timeline-renders-bounded-cards", phase.RenderedActionCount <= MacroPhaseView.StepsPerPage * 2 && macro.Script.Split('\n').Length == 8000, $"{phase.RenderedActionCount} cards, all 8000 steps retained");
        phase.NextPage();
        check("long-macro-timeline-navigation-retains-all-steps", phase.CurrentPage == 1 && macro.Script.Split('\n').Length == 8000, "Page navigation only changes rendering; the full program remains editable/exportable.");
        await capture("Macro-long-timeline-paged");
        await VerifyMacroDeletionAsync(page, tabs, capture, check);
        check("update-verification-no-input-engines", !vm.LuaRunning && !vm.HostMacrosRunning && !vm.LightsyncRunning, "All writes target in-memory HID++ replay; no physical handles or Windows input engines.");
    }

    private static async Task VerifyContinuousMacroAsync(MainPage page, Func<string, Task> capture, Action<string, bool, string> check)
    {
        var vm = page.ViewModel;
        vm.LoadCatalogVerificationFixture(MouseCatalog.ById("GPW")!);
        page.BeginNewMacro();
        vm.SelectedMacro!.Name = "持续循环验收";
        page.AdvanceNewMacroName();
        await capture("Macro-continuous-mode-choice");
        page.ChooseNewMacroType((int)MacroRepeat.Continuous);
        check("macro-creation-supports-continuous", vm.SelectedMacro?.Repeat == MacroRepeat.Continuous && vm.ShowMacroLoopHint, vm.MacroTypeDescription);
        page.CancelNewMacro();

        var replay = vm.AttachDiagnosticReplay();
        try
        {
            page.OpenLuaEditor();
            vm.LuaName = "板载持续循环";
            vm.LuaBoardTrigger = vm.LuaBoardTriggers.Single(t => t.Id == "4");
            vm.LuaBoardRepeat = vm.LuaBoardRepeats[0];
            vm.LuaSource = """
                function OnEvent(event, arg)
                  if event == "MOUSE_BUTTON_PRESSED" and arg == 4 then
                    while true do
                      PressMouseButton(1)
                      Sleep(17)
                      ReleaseMouseButton(1)
                      Sleep(33)
                    end
                  end
                end
                """;
            await vm.ConvertLuaToBoardCommand.ExecuteAsync(null);
            check("lua-continuous-infers-repeat-and-opens-editor", vm.SelectedMacro?.Name == vm.LuaName &&
                vm.SelectedMacro.Repeat == MacroRepeat.Continuous && vm.MacroTypeDescription.Contains("停止所有宏"), vm.LuaStatus);
            await capture("Macro-continuous-editor");

            vm.SelectedButton = vm.StandardBindings[4];
            vm.AssignPaletteItemCommand.Execute(new AssignmentItem("停止所有宏", "actions", "special", "macro-stop-all", ""));
            await vm.FlushImmediateSaveAsync();
            vm.MacroTarget = vm.StandardBindings[3];
            vm.BindMacroCommand.Execute(null);
            await vm.SaveMacrosCommand.ExecuteAsync(null);
            await vm.FlushImmediateSaveAsync();
            await vm.ReloadMacroLinksForVerificationAsync();
            var binding = vm.StandardBindings[3];
            var snapshot = await OnboardMemory.LoadBackupAsync(vm.LastBackupPath);
            snapshot.Sectors = replay.Sectors;
            var bytes = MacroAllocator.ReadMacroBytes(snapshot, new(ButtonBinding.MacroSector(binding.Original), ButtonBinding.MacroOffset(binding.Original)));
            check("continuous-onboard-auto-write-and-readback", !vm.HasError && replay.WriteStarts > 0 && binding.Value == vm.LuaName &&
                (binding.Original[0] >> 4) == 0 && vm.StandardBindings[4].Original[0] == 0x20 &&
                Convert.ToHexString(bytes) == "41000140001142000140002103FF", vm.Status);
        }
        finally { vm.DetachDiagnosticReplay(); }
    }
}
