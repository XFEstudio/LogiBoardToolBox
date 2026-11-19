using LogiBoardToolBox.Controls;
using LogiBoardToolBox.Core.Devices;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.ViewModels;
using LogiBoardToolBox.Views;
using Microsoft.UI.Xaml.Automation;

namespace LogiBoardToolBox.Utilities;

internal static partial class UiVerification
{
    private static async Task VerifyWorkspaceReviewAsync(MainPage page, WorkspaceHost tabs, Func<string, Task> capture,
        Action<string, bool, string> check)
    {
        var at = Array.IndexOf(args, "--workspace-review") + 1;
        if (at >= args.Length) throw new ArgumentException("需要离线板载快照目录。");
        var folder = args[at];
        var vm = page.ViewModel;
        var wired = await OnboardMemory.LoadBackupAsync(Path.Combine(folder, "g502x.json"));
        vm.LoadSaveRecoveryVerificationFixture(wired, MouseCatalog.ById("G502X")!);
        var usb = vm.AttachDiagnosticReplay(onboardFeature: 0x14, timeout: TimeSpan.FromMilliseconds(150));
        try
        {
            check("wired-model-and-slot", vm.DeviceModel.Id == "G502X" && vm.SelectedDevice?.DeviceIndex == 0xFF &&
                vm.StandardBindings.Count == 11 && vm.SelectedConfiguration?.Key == "board-2",
                "The wired G502 X has 11 bindings and opens captured onboard slot 2.");
            var macro = new MacroEditor { Name = "三键演示", Script = "mouse 1 down\ndelay 50\nmouse 1 up\nmouse 3 down\ndelay 20\nmouse 3 up\nmouse 2 down\ndelay 50\nmouse 2 up", UseStandardDelay = false };
            vm.Macros.Add(macro);
            vm.AssignmentSearch = macro.Name;
            var item = vm.AssignmentItems.Single();
            vm.AssignmentSearch = "";
            var duplicates = 0;
            usb.RepeatPreviousReadReply = (sector, offset) => sector == 0 && offset == 0xA0 && duplicates++ == 0;
            vm.SelectedButton = vm.StandardBindings[9];
            vm.AssignPaletteItemCommand.Execute(item);
            await vm.FlushImmediateSaveAsync();
            check("wired-stale-reply-save", duplicates > 1 && usb.WriteStarts > 0 && !vm.HasError &&
                vm.PersistenceStatus.Contains("读回一致"), vm.PersistenceStatus);
            await capture("01-assignments");

            page.OpenAssignmentItem(item);
            await capture("02-macro-editor");
            var press = Tree(page).OfType<MacroPhaseView>().Single(p => p.Segment == "press");
            check("visible-mouse-step-roles", new[] { "左键", "中键", "右键" }.All(name =>
                new[] { "按下", "抬起" }.All(direction => Tree(press).OfType<Button>()
                    .Any(b => AutomationProperties.GetName(b) == name + direction && b.Content is Canvas))),
                "Each mouse step has a distinct vector and a named button and direction.");

            usb.RepeatPreviousReadReply = (sector, offset) => sector == 0 && offset == 0xA0;
            var beforeFailure = usb.WriteStarts;
            macro.Script = macro.Script.Replace("delay 50", "delay 61", StringComparison.Ordinal);
            Invoke(page, "macro-save");
            await Until(() => vm.HasError && !vm.IsBusy, "board-read-timeout-in-editor");
            await capture("03-macro-save-error");
            check("macro-failure-keeps-edit-and-shows-retry", vm.OnboardSaveFailed && vm.Status.Contains("鼠标未响应板载读取") &&
                !vm.Status.Contains("设备 FF") && macro.Script.Contains("delay 61") && usb.WriteStarts == beforeFailure
                && Tree(page).OfType<TextBlock>().Any(t => t.Visibility == Visibility.Visible &&
                    AutomationProperties.GetAutomationId(t) == "macro-save-error" && t.ActualWidth > 0),
                vm.Status);

            usb.RepeatPreviousReadReply = null;
            Invoke(page, "macro-save");
            await Until(() => tabs.SelectedIndex == 0 && !vm.HasError && vm.PersistenceStatus.Contains("读回一致"), "board-editor-retry");
            var binding = new MouseProfile(2, true, usb.Sectors[2], profileFormat: 4).GetBinding(9);
            var address = new MacroLocation(ButtonBinding.MacroSector(binding), ButtonBinding.MacroOffset(binding));
            var stored = new MemorySnapshot { Description = wired.Description, Sectors = usb.Sectors };
            check("retry-wrote-exact-current-draft", MacroAllocator.ReadMacroBytes(stored, address).SequenceEqual(macro.Compile().Bytes),
                "The changed 61 ms timings survived retry and readback.");
            tabs.SelectedIndex = 2;
            await capture("04-dpi");
        }
        finally { vm.DetachDiagnosticReplay(); }

        var wireless = await OnboardMemory.LoadBackupAsync(Path.Combine(folder, "g304.json"));
        vm.LoadSaveRecoveryVerificationFixture(wireless, MouseCatalog.ById("G305")!);
        tabs.SelectedIndex = 0;
        var receiver = vm.AttachDiagnosticReplay();
        try
        {
            await vm.ReadDiagnosticReplayStorageAsync();
            var macro = new MacroEditor { Name = "按键时序", Script = "key A down\ndelay 13\nkey A up", UseStandardDelay = false };
            vm.Macros.Add(macro);
            vm.SelectedMacro = macro;
            vm.MacroTarget = vm.StandardBindings[4];
            vm.BindMacroCommand.Execute(null);
            await vm.FlushImmediateSaveAsync();
            var changed = receiver.Sectors.Keys.Where(s => !receiver.Sectors[s].SequenceEqual(wireless.Sectors[s])).Order().ToArray();
            var binding = new MouseProfile(1, true, receiver.Sectors[1], profileFormat: 3).GetBinding(4);
            check("g304-save-uses-actual-range", !vm.HasError && receiver.WriteStarts == 2 &&
                changed.SequenceEqual([1, 2]) && ButtonBinding.MacroSector(binding) == 2, vm.PersistenceStatus);
            await capture("05-g304-saved");
        }
        finally { vm.DetachDiagnosticReplay(); }

        vm.LoadCatalogVerificationFixture(MouseCatalog.ById("G502XPlus")!);
        vm.SelectedButton = vm.StandardBindings[0];
        await capture("06-assignments-plus");
        vm.IsSideView = true;
        vm.ShowGShift = true;
        await capture("07-side-view-gshift");
        var map = Tree(page).OfType<MouseMapView>().Single();
        var buttons = Tree(map).OfType<Button>().Where(b => b.Tag is ButtonBindingEditor);
        check("side-preview-exposes-three-shift-bindings", buttons.Select(b => (ButtonBindingEditor)b.Tag)
                .Distinct().OrderBy(b => b.Index).Select(b => (b.Index, b.GShift))
                .SequenceEqual([(3, true), (4, true), (5, true)]),
            "Side view exposes the three G-Shift bindings.");
        vm.IsSideView = false;
        vm.ShowGShift = false;
        tabs.SelectedIndex = 3;
        await capture("08-lightsync");

        tabs.SelectedIndex = 0;
        var first = vm.Macros[0];
        var duplicate = new MacroEditor { Name = first.Name, Script = "tap A" };
        vm.Macros.Add(duplicate);
        vm.AssignmentSearch = first.Name;
        var secondRow = vm.AssignmentItems.Last();
        vm.AssignmentSearch = "";
        page.OpenAssignmentItem(secondRow);
        check("duplicate-macro-editor-opens-clicked-row", ReferenceEquals(vm.SelectedMacro, duplicate) && tabs.SelectedIndex == 1,
            "The second historical duplicate opens for editing instead of throwing a Single-match exception.");
        page.CancelNewMacro();
        vm.Macros.Remove(duplicate);
        page.BeginNewMacro();
        await capture("09-new-macro");
        vm.SelectedMacro!.Name = "按键组合";
        page.AdvanceNewMacroName();
        await capture("10-macro-type");
        page.CancelNewMacro();
        tabs.SelectedIndex = 4;
        await capture("11-device-storage");
        check("no-input-engines", !vm.HostMacrosRunning && !vm.LightsyncRunning,
            "All actions used simulated HID reports and internal WinUI controls.");
    }
}
