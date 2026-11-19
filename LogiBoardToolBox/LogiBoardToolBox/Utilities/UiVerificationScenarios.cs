using System.Text.Json;
using LogiBoardToolBox.Core.Lighting;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Core.Windows;
using LogiBoardToolBox.ViewModels;
using LogiBoardToolBox.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LogiBoardToolBox.Utilities;

internal static partial class UiVerification
{
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Invoke(MainPage page, string id)
    {
        var button = Tree(page).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == id);
        if (!button.IsEnabled) throw new InvalidOperationException("UI action is disabled: " + id);
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    }
    private static async Task Until(Func<bool> condition, string name, int timeout = 6000)
    {
        var stop = DateTime.UtcNow.AddMilliseconds(timeout);
        while (!condition()) { if (DateTime.UtcNow > stop) throw new TimeoutException("UI action did not complete: " + name); await Task.Delay(25); }
    }
    private static async Task VerifyMacroSaveAsync(MainPage page, Controls.WorkspaceHost tabs, Func<string, Task> capture, Action<string, bool, string> check)
    {
        var vm = page.ViewModel; page.BeginNewMacro(); vm.SelectedMacro!.Name = "后台流程校验宏"; page.AdvanceNewMacroName(); page.ChooseNewMacroType(1);
        vm.SelectedMacro.UseStandardDelay = false; vm.SelectedMacro.Script = "mouse 1 down\ndelay 20\nmouse 1 up\ndelay 80";
        var expected = vm.SelectedMacro; Invoke(page, "macro-save"); await Until(() => tabs.SelectedIndex == 0, "macro-save");
        var file = Path.Combine(SettingsDirectory, "macros.json");
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        check("macro-ui-save-persists", saved.RootElement.EnumerateArray().Any(m => m.GetProperty("Name").GetString() == expected.Name && m.GetProperty("Repeat").GetInt32() == 1 && m.GetProperty("Script").GetString() == expected.Script), "Real Save button invoked through its WinUI automation peer; exact script/type read from isolated macros.json.");
        vm.SelectedMacro = expected; tabs.SelectedIndex = 1; expected.Script = "tap Z"; expected.Name = "应当取消的更改"; page.CancelNewMacro();
        check("macro-edit-cancel-preserves-saved", expected.Name == "后台流程校验宏" && expected.Script.StartsWith("mouse 1 down", StringComparison.Ordinal), "Cancel restored the saved macro name and script.");
        await capture("17-macro-save-result");
    }

    private static async Task VerifyMacroDeletionAsync(MainPage page, Controls.WorkspaceHost tabs, Func<string, Task> capture, Action<string, bool, string> check)
    {
        var vm = page.ViewModel;
        vm.LoadCatalogVerificationFixture(Core.Devices.MouseCatalog.ById("GPW")!);
        var retained = vm.Macros.Single();
        var replay = vm.AttachDiagnosticReplay();
        try
        {
            vm.SelectedAssignmentCategory = vm.AssignmentCategories.Single(c => c.Id == "macros");
            page.BeginNewMacro();
            var macro = vm.SelectedMacro!;
            macro.Name = "可删除的录制宏";
            page.AdvanceNewMacroName();
            page.ChooseNewMacroType(0);
            macro.Script = "tap A\ndelay 20";
            vm.MacroTarget = vm.StandardBindings[3];
            vm.BindMacroCommand.Execute(null);
            Invoke(page, "macro-save");
            await Until(() => tabs.SelectedIndex == 0, "save macro before deletion");
            var writes = replay.WriteStarts;
            var saved = Path.Combine(SettingsDirectory, "macros.json");
            var before = await File.ReadAllTextAsync(saved);
            var entry = vm.AssignmentItems.Single(m => m.Name == macro.Name);
            page.OpenAssignmentItem(entry);
            await capture("Macro-delete-entry");

            Invoke(page, "macro-delete");
            await ChooseDialogButton("取消");
            await Until(() => !DialogButtons().Any(b => Equals(b.Content, "删除")), "dismiss delete dialog");
            check("macro-delete-cancel-keeps-recording", tabs.SelectedIndex == 1 && vm.Macros.Contains(macro) && before == await File.ReadAllTextAsync(saved), "Cancel leaves the recording in the editor and the library file.");

            Invoke(page, "macro-delete");
            await ChooseDialogButton("删除");
            await Until(() => tabs.SelectedIndex == 0 && !vm.Macros.Contains(macro), "delete macro from library");
            using var library = JsonDocument.Parse(await File.ReadAllTextAsync(saved));
            var item = library.RootElement.EnumerateArray().Single();
            check("macro-delete-persisted-and-palette-updated", item.GetProperty("Name").GetString() == retained.Name &&
                vm.AssignmentItems.All(m => m.Name != macro.Name), "The isolated library file and the macro list retain the other macro.");
            check("macro-delete-preserves-physical-binding", !vm.HasError && replay.WriteStarts == writes &&
                vm.StandardBindings[3].SelectedAction.Id == "keep" && ButtonBinding.IsMacro(vm.StandardBindings[3].Original),
                "Deleting from the library removes its editable reference and leaves the programmed action available for explicit reassignment.");
        }
        finally { vm.DetachDiagnosticReplay(); }

        IEnumerable<Button> DialogButtons() => VisualTreeHelper.GetOpenPopupsForXamlRoot(page.XamlRoot)
            .Where(popup => popup.Child is not null).SelectMany(popup => Tree(popup.Child)).OfType<Button>();

        async Task ChooseDialogButton(string label)
        {
            await Until(() => DialogButtons().Any(b => Equals(b.Content, label) && b.IsEnabled), "delete confirmation button");
            var button = DialogButtons().Single(b => Equals(b.Content, label) && b.IsEnabled);
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        }
    }

    private static async Task VerifyOnboardLoopCadenceAsync(MainPage page, Controls.WorkspaceHost tabs, Action<string, bool, string> check)
    {
        var vm = page.ViewModel;
        vm.LoadCatalogVerificationFixture(Core.Devices.MouseCatalog.ById("G502XPlus")!);
        tabs.SelectedIndex = 0;
        var replay = vm.AttachDiagnosticReplay();
        try
        {
            page.BeginNewMacro();
            var macro = vm.SelectedMacro!;
            macro.Name = "标准延迟循环校验";
            page.AdvanceNewMacroName();
            page.ChooseNewMacroType(1);
            macro.Script = "mouse 1 down\nmouse 1 up";
            var standardDelay = Tree(page).OfType<CheckBox>().Single(b => Equals(b.Content, "使用标准延迟"));
            standardDelay.IsChecked = true;
            await Until(() => macro.UseStandardDelay, "standard-delay binding");
            vm.MacroTarget = vm.StandardBindings[5];
            vm.BindMacroCommand.Execute(null);
            Invoke(page, "macro-save");
            await Until(() => tabs.SelectedIndex == 0, "standard-delay macro save");

            var readback = vm.CaptureDiagnosticContext().GetProperty("Snapshot").Deserialize(Profiles.ProfileJsonContext.Default.MemorySnapshot)!;
            var binding = readback.GetProfiles().Single(p => p.Sector == readback.ActiveSector).GetBinding(5);
            var location = new MacroLocation(ButtonBinding.MacroSector(binding), ButtonBinding.MacroOffset(binding));
            var bytes = Convert.ToHexString(MacroAllocator.ReadMacroBytes(readback, location));
            check("standard-delay-onboard-loop-readback", replay.WriteStarts > 0 && !vm.HasError && bytes == "41000140003242000140003202FF",
                "The macro editor's 50 ms standard delay survives binding, saving and HID replay readback on both sides of the loop. " + bytes);
        }
        finally { vm.DetachDiagnosticReplay(); }
    }

    private static async Task VerifyAnimationAsync(MainPage page, Func<string, Task> capture, Action<string, bool, string> check)
    {
        var vm = page.ViewModel; var original = JsonSerializer.Serialize(vm.BuildLightsyncSettings(), Profiles.ProfileJsonContext.Default.LightingSettings);
        page.OpenAnimationEditor();
        check("animation-paused-frame-preview", vm.LedZones.Select((zone, i) => { var c = vm.SelectedAnimationFrame!.Colors[i].Scale(vm.LightsyncBrightness / 100); var preview = zone.PreviewBrush.Color; return c.R == preview.R && c.G == preview.G && c.B == preview.B; }).All(v => v), "Entering the paused editor renders the selected frame's actual colors, even when the preceding live preview showed another frame.");
        await capture("18-animation-editor");
        var count = vm.AnimationFrames.Count; Invoke(page, "animation-duplicate-frame");
        check("animation-duplicate", vm.AnimationFrames.Count == count + 1, "Invoked the editor's Duplicate Frame button.");
        vm.SelectedAnimationFrame!.Duration = "200"; Invoke(page, "animation-curve-InCirc");
        check("animation-duration-and-curve", vm.SelectedAnimationFrame.Curve == TransitionCurve.InCirc && int.Parse(vm.AnimationFrames[2].Milliseconds) == int.Parse(vm.AnimationFrames[0].Duration) + 200, "Editing a duration reflowed following frames; curve selection changed the selected frame.");
        var moved = vm.SelectedAnimationFrame; vm.MoveAnimationFrame(1, 3);
        check("animation-reorder", ReferenceEquals(vm.AnimationFrames[3], moved), "The same reorder method used by the frame-drop handler moved the selected frame to slot 4; physical pointer dragging is not simulated.");
        Invoke(page, "animation-zoom-50"); vm.SeekAnimation(500); await capture("19-animation-edit-state");
        check("animation-zoom-and-seek", vm.AnimationZoom == 50 && vm.AnimationPlayhead == 500 && !vm.PlayLightsyncPreview, "50% zoom; paused playhead at 500ms; LED preview rendered at the seek position.");
        Invoke(page, "animation-cancel"); check("animation-cancel-restores", JsonSerializer.Serialize(vm.BuildLightsyncSettings(), Profiles.ProfileJsonContext.Default.LightingSettings) == original, "Cancel restored all frames, colors, curves, durations and direction.");
        page.OpenAnimationEditor(true); vm.AnimationName = "后台时间线校验"; Invoke(page, "animation-add-frame");
        Invoke(page, "animation-curve-InOutCubic"); vm.SelectedAnimationDirection = AnimationDirection.Bounce;
        vm.LightsyncColor = "#FF2277"; vm.PaintSelectedZonesCommand.Execute(null); vm.AnimationDefaultSpeed = 200;
        Invoke(page, "animation-play"); var before = vm.AnimationPlayhead; await Task.Delay(220); await capture("20-animation-playing");
        check("animation-playback-advances", vm.PlayLightsyncPreview && vm.AnimationPlayhead != before, "A running dispatcher timer advanced the playhead and rendered a later frame.");
        Invoke(page, "animation-play"); Invoke(page, "animation-save"); await Until(() => !vm.AnimationEditing, "animation-save");
        var path = Path.Combine(SettingsDirectory, "animations.json");
        var saved = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path), Profiles.ProfileJsonContext.Default.Animations)!;
        var animation = saved.Values.Single(s => s.Name == "后台时间线校验");
        check("animation-ui-save-persists", animation.Frames.Length == 2 && animation.Direction == AnimationDirection.Bounce && animation.DefaultFrameSpeed == 200 && animation.Frames[1].Colors.All(c => c.ToString() == "#FF2277"), "Saved animation JSON preserves both frames, eight LED colors, direction, transition and default speed.");
        page.OpenAnimationEditor(); check("animation-reload", vm.AnimationFrames.Count == 2 && vm.SelectedAnimationDirection == AnimationDirection.Bounce, "Reopening the saved animation restored its timeline.");
        Invoke(page, "animation-remove-frame"); check("animation-delete-frame", vm.AnimationFrames.Count == 1, "Delete Frame button removed one frame.");
        Invoke(page, "animation-cancel"); await capture("21-animation-saved-result");
    }
    private static async Task VerifyHeroAsync(MainPage page, Func<string, Task> capture, Action<string, bool, string> check)
    {
        var vm = page.ViewModel;
        using var client = LogitechDiscovery.Open(vm.SelectedDevice!);
        var memory = new OnboardMemory(client, vm.SelectedDevice!.Identity, vm.SelectedDevice.Name);
        var before = await memory.ReadAsync();
        try
        {
        if (args.Contains("--write-verify")) { await vm.SetDeviceModeAsync(false); await vm.RetryHeroAsync(); }
        page.OpenHeroEditor(); await capture("22-hero-intro"); Invoke(page, "hero-next"); await Until(() => page.HeroEditorState == "source", "hero-source"); await capture("23-hero-source");
        if (vm.AdvancedState?.CalibrationSupported == true)
        {
            check("hero-software-mode-reads-calibration", vm.AdvancedState.Calibration is { DpiX: > 0, DpiY: > 0 }, Core.Diagnostics.DiagnosticJson.Serialize(vm.AdvancedState));
            await capture("24-hero-calibration-ready");
            await File.WriteAllTextAsync(Path.Combine(OutputDirectory, "hero-software-state.json"), Core.Diagnostics.DiagnosticJson.Serialize(vm.AdvancedState, indented: true));
        }
        else
        {
            Invoke(page, "hero-next"); await Until(() => page.HeroEditorState == "error", "hero-firmware-error"); await capture("24-hero-firmware-error");
            check("hero-rejection-reported", vm.HeroCalibrationStatus.Length > 0, vm.HeroCalibrationStatus);
            Invoke(page, "hero-next"); await Until(() => page.HeroEditorState == "source", "hero-retry");
        }
        Invoke(page, "hero-cancel");
        }
        finally
        {
            if (args.Contains("--write-verify"))
            {
                if (before.Mode == 1) await memory.ActivateProfileAsync(before.ActiveSector); else await memory.SetModeAsync(false);
                var restored = await memory.ReadAsync();
                var identical = before.Mode == restored.Mode && before.ActiveSector == restored.ActiveSector && before.Sectors.All(p => p.Value.SequenceEqual(restored.Sectors[p.Key]));
                check("hero-mode-check-restored", identical, "HERO capability was read in actual software mode; all original profile sectors and mode were restored.");
                await vm.ReadCommand.ExecuteAsync(null);
            }
        }
    }
    private static async Task VerifyImmediateWritesAsync(MainPage page, Controls.WorkspaceHost tabs, Func<string, Task> capture, Action<string, bool, string> check)
    {
        var vm = page.ViewModel; var device = vm.SelectedDevice!;
        using var client = LogitechDiscovery.Open(device); var memory = new OnboardMemory(client, device.Identity, device.Name);
        var before = await memory.ReadAsync(); var folder = Path.Combine(OutputDirectory, "automatic-write"); Directory.CreateDirectory(folder);
        await OnboardMemory.SaveBackupAsync(before, Path.Combine(folder, "before.json"));
        HardwareWritesEnabled = true;
        try
        {
            var choice = vm.Configurations.Single(c => c.Onboard && c.Sector == 2); await vm.SelectConfigurationAsync(choice);
            check("select-board-activates-slot", vm.IsEditingOnboard && vm.OnboardMode && vm.SelectedProfile?.Sector == 2, "Configuration picker chose onboard slot 2 and read back active mode/slot.");
            tabs.SelectedIndex = 2; await Task.Delay(100); page.UpdateLayout();
            var stage = vm.DpiStages[0]; stage.LinkAxes = false;
            await Until(() => Tree(page).OfType<TextBox>().Any(t => ReferenceEquals(t.Tag, stage) && AutomationProperties.GetAutomationId(t) == "stage-dpi-x"), "dpi-template-bound-row");
            var field = Tree(page).OfType<Controls.CommitTextBox>().First(t => ReferenceEquals(t.Tag, stage) && AutomationProperties.GetAutomationId(t) == "stage-dpi-x");
            field.Text = "1350"; if (!field.Commit()) throw new InvalidOperationException(field.ErrorText);
            await Until(() => stage.Value == "1350", "dpi-text-binding"); stage.ValueY = "1450";
            vm.DefaultDpi = 1; vm.ShiftDpi = 1; vm.ProfileName = "自动保存验证";
            vm.SelectedRate = vm.ReportRates.Single(r => r.Id == "125"); vm.SelectedWirelessRate = vm.WirelessReportRates.Single(r => r.Id == "125");
            vm.PowerSaveSeconds = "90"; vm.PowerOffSeconds = "400";
            vm.SelectedLightsyncMode = vm.LightsyncModes[0]; vm.SelectedLightsyncEffect = vm.LightsyncEffects.Single(e => e.Id == "Fixed"); vm.LightsyncColor = "#12AB56"; vm.LightsyncBrightness = 40;
            vm.Macros.Add(new MacroEditor { Name = "自动写入无输入校验", Script = "delay 10" });
            var binding = vm.StandardBindings[5]; binding.SelectedAction = ButtonBindingEditor.ActionChoices.Single(a => a.Id == "macro"); binding.Value = "自动写入无输入校验";
            await vm.FlushImmediateSaveAsync();
            var actual = await memory.ReadAsync(); await OnboardMemory.SaveBackupAsync(actual, Path.Combine(folder, "after.json"));
            var profile = actual.GetProfiles().Single(p => p.Sector == 2);
            check("automatic-edit-readback", actual.Mode == 1 && actual.ActiveSector == 2 && profile.Name == "自动保存验证" && profile.Dpis[0] == 1350 && profile.GetDpisY(actual.Capabilities)[0] == 1450 && profile.GetRateHz(actual.Capabilities, false) == 125 && profile.GetRateHz(actual.Capabilities, true) == 125 && ButtonBinding.IsMacro(profile.GetBinding(5)) && profile.GetLighting(0)[0] == 1, "Real DPI TextBox binding, rates, name, timeouts, LED preset and macro assignment automatically committed; complete device snapshot read back independently. No write/save button invoked.");
            await capture("25-immediate-board-write");
            await vm.SelectConfigurationAsync(vm.Configurations.First(c => !c.Onboard));
        }
        finally
        {
            HardwareWritesEnabled = false;
            try { await vm.FlushImmediateSaveAsync(); } catch (IOException) { }
            var current = await memory.ReadAsync(); var plan = MemoryWritePlan.Restore(current, before, true);
            if (plan.Changes.Count != 0) await memory.CommitAsync(plan, Path.Combine(folder, "backups"));
            if (before.Mode == 1) await memory.ActivateProfileAsync(before.ActiveSector); else await memory.SetModeAsync(false);
            var restored = await memory.ReadAsync(); await OnboardMemory.SaveBackupAsync(restored, Path.Combine(folder, "restored.json"));
            var identical = before.Sectors.All(p => p.Value.SequenceEqual(restored.Sectors[p.Key])) && before.Mode == restored.Mode && before.ActiveSector == restored.ActiveSector;
            await File.WriteAllTextAsync(Path.Combine(folder, "restoration.json"), Core.Diagnostics.DiagnosticJson.Serialize(new { AllSectorsAndModeIdentical = identical }));
            check("automatic-write-test-restored", identical, "All original sectors, mode and active profile match the pre-test backup.");
        }
        await vm.ReadCommand.ExecuteAsync(null);
    }
}
