using System.Text.Json;
using LogiBoardToolBox.Controls;
using LogiBoardToolBox.Core.Devices;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Profiles;
using LogiBoardToolBox.ViewModels;
using LogiBoardToolBox.Views;
using Microsoft.UI.Xaml.Automation;

namespace LogiBoardToolBox.Utilities;

internal static partial class UiVerification
{
    private static async Task VerifyLiveDpiAsync(MainPage page, WorkspaceHost tabs, Func<string, Task> capture, Action<string, bool, string> check)
    {
        var index = Array.IndexOf(args, "--live-dpi-replay") + 1;
        if (index >= args.Length) throw new ArgumentException("需要诊断回放目录。");
        var folder = args[index];
        var snapshot = await OnboardMemory.LoadBackupAsync(Path.Combine(folder, "snapshot.json"));
        var sensor = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(folder, "sensor.json")), ProfileJsonContext.Default.SensorPerformance)!;
        var reports = (await File.ReadAllLinesAsync(Path.Combine(folder, "switch-reports.txt"))).Select(Convert.FromHexString).ToArray();
        var vm = page.ViewModel;
        vm.LoadAdvancedDpiDiagnosticSnapshot(snapshot, sensor, "Superlight2c");
        var replay = await vm.AttachDpiDiagnosticReplayAsync();
        try
        {
            tabs.SelectedIndex = 2;
            var editing = vm.SelectedDpiStage;
            var requests = replay.Requests;
            check("readback-current-dpi-differs-from-default", vm.DpiStages[0].IsCurrent && vm.DefaultDpi == 3 && vm.PerformanceSummary.Contains("750 × 750"), vm.PerformanceSummary);
            await capture("2c-current-750");
            foreach (var (firstReport, stage, dpi) in new[] { (0, 1, 800), (2, 2, 900), (4, 0, 750) })
            {
                replay.PostReport(reports[firstReport]);
                replay.PostReport(reports[firstReport + 1]);
                await Until(() => vm.DpiStages[stage].IsCurrent && vm.PerformanceSummary.Contains($"{dpi} × {dpi}"), $"live DPI {dpi}");
                check($"live-dpi-{dpi}", vm.DpiStages.Count(s => s.IsCurrent) == 1 && ReferenceEquals(editing, vm.SelectedDpiStage), vm.PerformanceSummary);
                if (dpi != 750) await capture($"2c-current-{dpi}");
            }
            await vm.FlushImmediateSaveAsync();
            check("dpi-events-have-no-device-writes-or-requests", replay.WriteStarts == 0 && replay.Requests == requests && vm.DefaultDpi == 3 && vm.ShiftDpi == 1,
                "Captured notifications update presentation without changing the edited row, DPI settings or device.");

            tabs.SelectedIndex = 0;
            await capture("2c-side-buttons");
            vm.SelectedButton = vm.StandardBindings[2];
            vm.ResetSelectedButtonCommand.Execute(null);
            await vm.FlushImmediateSaveAsync();
            check("2c-reset-middle-button-readback", !vm.HasError && vm.StandardBindings[2].Original.SequenceEqual(ButtonBinding.Mouse(3)), vm.Status);
            vm.AssignPaletteItemCommand.Execute(new AssignmentItem("DPI 循环", "actions", "special", "dpi-cycle", ""));
            await vm.FlushImmediateSaveAsync();
            check("2c-dpi-cycle-written-to-middle-button", !vm.HasError && vm.StandardBindings[2].Original.SequenceEqual(ButtonBinding.Special(5)) &&
                replay.Sectors.All(p => p.Value.SequenceEqual(snapshot.Sectors[p.Key])), "Remapping restores the exact captured DPI-cycle binding and leaves the other bytes intact.");
        }
        finally { vm.DetachDiagnosticReplay(); }
    }

    private static async Task VerifyMouseButtonPresentationAsync(MainPage page, WorkspaceHost tabs, Func<string, Task> capture, Action<string, bool, string> check)
    {
        var vm = page.ViewModel;
        var map = Tree(page).OfType<MouseMapView>().Single();
        int[] MappedButtons() => Tree(map).OfType<Button>().Select(b => b.Tag).OfType<ButtonBindingEditor>().Select(r => r.Index).Distinct().Order().ToArray();
        check("superlight-2c-five-editable-buttons", MappedButtons().SequenceEqual(Enumerable.Range(0, 5)) && vm.DeviceModel.Layout == "Compact", "Both left-side buttons have selectable controls in view 1.");
        foreach (var id in new[] { "G502X", "G502XLightspeed", "G502XMillennium" })
        {
            vm.LoadCatalogVerificationFixture(MouseCatalog.ById(id)!);
            check(id + "-side-buttons-in-view-one", new[] { 3, 4, 5 }.All(MappedButtons().Contains), "G4, G5 and G6 remain editable when no side asset is provided.");
            if (id == "G502X") await capture("G502X-side-buttons");
        }
        vm.LoadCatalogVerificationFixture(MouseCatalog.ById("G502XPlus")!);
        vm.IsSideView = true;
        check("G502XPlus-view-two-editable", vm.HasModelSideView && MappedButtons().SequenceEqual(new[] { 3, 4, 5 }), "The existing side view still edits G4, G5 and G6.");
        vm.IsSideView = false;

        page.BeginNewMacro();
        var macro = vm.SelectedMacro!;
        macro.Name = "鼠标按键图标校验";
        page.AdvanceNewMacroName();
        page.ChooseNewMacroType(1);
        macro.Script = "mouse 1 down\ndelay 50\nmouse 1 up";
        await capture("macro-reference-left-click");
        macro.Script = "mouse 1 down\ndelay 50\nmouse 1 up\ndelay 50\nmouse 3 down\ndelay 50\nmouse 3 up\ndelay 50\nmouse 2 down\ndelay 50\nmouse 2 up";
        await capture("macro-mouse-buttons");
        var phase = Tree(page).OfType<MacroPhaseView>().Single(p => p.Segment == "press");
        var buttons = Tree(phase).OfType<Button>().ToArray();
        foreach (var name in new[] { "左键", "中键", "右键" })
            check("macro-icon-" + name, new[] { "按下", "抬起" }.All(direction => buttons.Any(b => b.Content is Canvas && AutomationProperties.GetName(b) == name + direction)),
                "Distinct WinUI mouse visuals and accessible names for both directions.");
        check("macro-mouse-hid-roles-preserved", Convert.ToHexString(macro.Compile().Bytes) == "41000140003242000140003241000440003242000440003241000240003242000202FF",
            "Left, wheel and right use masks 1, 4 and 2. All five explicit waits survive.");
        page.CancelNewMacro();

        tabs.SelectedIndex = 4;
        await capture("portable-settings-icons");
        var settings = Tree(page).OfType<Grid>().Single(g => g.Name == "SettingsOverlay");
        var back = Tree(settings).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "返回");
        check("settings-back-is-vector", back.Content is PathIcon { Data: not null, ActualWidth: > 0 }, "The settings back button renders from compiled vector geometry without a symbol font.");
        tabs.SelectedIndex = 2;
        var dpi = Tree(page).OfType<DpiProfileView>().Single();
        check("dpi-tools-are-vector", Tree(dpi).OfType<Button>().Count(b => b.Content is PathIcon { Data: not null }) == 6, "Search, reset, rename, copy and delete are vector controls.");
    }
}
