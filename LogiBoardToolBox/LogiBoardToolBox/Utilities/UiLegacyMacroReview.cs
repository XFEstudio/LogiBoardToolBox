using LogiBoardToolBox.Controls;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Views;

namespace LogiBoardToolBox.Utilities;

internal static partial class UiVerification
{
    private static async Task VerifyG102MacroSaveAsync(MainPage page, WorkspaceHost tabs, Func<string, Task> capture, Action<string, bool, string> check)
    {
        var at = Array.IndexOf(args, "--g102-snapshot");
        if (at + 1 >= args.Length) throw new ArgumentException("需要 G102 LIGHTSYNC 离线快照路径。");
        var snapshot = await OnboardMemory.LoadBackupAsync(args[at + 1]);
        if (snapshot.Capabilities.ProfileFormat != 4 || !snapshot.Capabilities.IsRawMacroSector(2))
            throw new InvalidDataException("不是已核对的 G102 LIGHTSYNC 板载布局。");

        var vm = page.ViewModel;
        vm.LoadLegacyDiagnosticSnapshot(snapshot);
        tabs.SelectedIndex = 0;
        var replay = vm.AttachDiagnosticReplay();
        try
        {
            var macro = vm.SelectedMacro!;
            vm.MacroTarget = vm.StandardBindings[3];
            vm.BindMacroCommand.Execute(null);
            await vm.FlushImmediateSaveAsync();
            check("G102-new-macro-saved", !vm.HasError && replay.WriteStarts == 2 && vm.PersistenceStatus.Contains("读回一致"),
                vm.Status);

            var assignments = vm.AssignmentCategories.Single(choice => choice.Id == "macros");
            vm.SelectedAssignmentCategory = assignments;
            page.OpenAssignmentItem(vm.AssignmentItems.Single(item => item.Name == macro.Name));
            macro.Script = "wheel 1\ndelay 37";
            Invoke(page, "macro-save");
            await Until(() => (tabs.SelectedIndex == 0 || vm.HasError) && vm.IsIdle, "G102 edited macro save");
            check("G102-edited-macro-saved", !vm.HasError && tabs.SelectedIndex == 0 && replay.WriteStarts == 4 &&
                vm.PersistenceStatus.Contains("读回一致"), vm.Status);

            var readback = new MemorySnapshot { Description = snapshot.Description, Sectors = replay.Sectors };
            var before = snapshot.GetProfiles().Single();
            var after = readback.GetProfiles().Single();
            var binding = after.GetBinding(3);
            var target = new MacroLocation(ButtonBinding.MacroSector(binding), ButtonBinding.MacroOffset(binding));
            check("G102-exact-bytecode-and-original-macro-page", Convert.ToHexString(MacroAllocator.ReadMacroBytes(readback, target)) == "2001400025FF" &&
                snapshot.Sectors[2].SequenceEqual(replay.Sectors[2]) && Enumerable.Range(0, 6).All(b => before.GetBinding(b, true).SequenceEqual(after.GetBinding(b, true))),
                "The edited macro preserves 37ms and the capture's raw page and G-Shift bindings.");
            await capture("G102-macro-saved");
        }
        finally { vm.DetachDiagnosticReplay(); }
    }

    private static async Task VerifyLightspeedMacroSaveAsync(MainPage page, WorkspaceHost tabs, Func<string, Task> capture, Action<string, bool, string> check)
    {
        var at = Array.IndexOf(args, "--lightspeed-snapshot");
        if (at < 0 || at + 1 >= args.Length) throw new ArgumentException("需要 G502 LIGHTSPEED 离线快照路径。");
        var snapshot = await OnboardMemory.LoadBackupAsync(args[at + 1]);
        if (snapshot.Capabilities.ProfileFormat != 3 || snapshot.Capabilities.SectorSize != 255)
            throw new InvalidDataException("不是此次格式 3 / 255 字节诊断快照。");
        var vm = page.ViewModel;
        vm.LoadLegacyDiagnosticSnapshot(snapshot); tabs.SelectedIndex = 0;
        var original = snapshot.Sectors.ToDictionary(p => p.Key, p => Convert.ToHexString(p.Value));
        var oldProgram = MacroAllocator.ReadMacroBytes(snapshot, new(6, 34));
        var replay = vm.AttachDiagnosticReplay();
        try
        {
            vm.MacroTarget = vm.StandardBindings[3]; vm.BindMacroCommand.Execute(null);
            await vm.FlushImmediateSaveAsync();
            check("G502Wireless-save-readback", !vm.HasError && replay.WriteStarts == 2 && vm.PersistenceStatus.Contains("读回一致"), vm.Status);
            check("G502Wireless-existing-pages-preserved", original.Where(p => p.Key is not (1 or 7))
                .All(p => p.Value == Convert.ToHexString(replay.Sectors[p.Key])), "Only new macro page 7 and selected profile 1 change.");
            var current = new MemorySnapshot { Description = snapshot.Description, Sectors = replay.Sectors };
            var binding = current.GetProfiles()[0].GetBinding(3);
            check("G502Wireless-new-macro-delay", ButtonBinding.MacroSector(binding) == 7 &&
                Convert.ToHexString(MacroAllocator.ReadMacroBytes(current, new(7, ButtonBinding.MacroOffset(binding)))) == "430106440106400050430119440119FF",
                "The reported copy/paste draft retains its 80ms delay.");
            check("G502Wireless-old-gshift-delay", MacroAllocator.ReadMacroBytes(current, new(6, 34)).SequenceEqual(oldProgram) &&
                Convert.ToHexString(current.GetProfiles()[1].GetBinding(6, true)) == "00060022", "Existing page 6, its timing and G-Shift binding are untouched.");
            check("G502Wireless-no-input-engines", !vm.HostMacrosRunning && !vm.LightsyncRunning, "In-memory HID replay only; no physical access, hooks or generated input.");
            await capture("G502Wireless-macro-saved");
            tabs.SelectedIndex = 1; await capture("G502Wireless-macro-editor");
        }
        finally { vm.DetachDiagnosticReplay(); }
    }

    private static async Task VerifyLegacyAndBindingLabelsAsync(MainPage page, WorkspaceHost tabs, Func<string, Task> capture, Action<string, bool, string> check)
    {
        var vm = page.ViewModel;
        var at = Array.IndexOf(args, "--legacy-snapshot");
        if (at >= 0)
        {
            if (at + 1 >= args.Length) throw new ArgumentException("需要离线快照路径。");
            var snapshot = await OnboardMemory.LoadBackupAsync(args[at + 1]);
            vm.LoadLegacyDiagnosticSnapshot(snapshot); tabs.SelectedIndex = 0;
            var original = (byte[])snapshot.Sectors[6].Clone();
            var oldProfiles = snapshot.Sectors.Where(s => s.Key is >= 2 and <= 5).ToDictionary(p => p.Key, p => Convert.ToHexString(p.Value));
            var replay = vm.AttachDiagnosticReplay();
            try
            {
                vm.MacroTarget = vm.StandardBindings[3]; vm.BindMacroCommand.Execute(null);
                await vm.FlushImmediateSaveAsync();
                check("G502Hero-captured-raw-page-save", !vm.HasError && replay.WriteStarts == 2 && vm.PersistenceStatus.Contains("读回一致"), vm.Status);
                check("G502Hero-native-page-and-other-profiles-preserved", original.SequenceEqual(replay.Sectors[6]) && oldProfiles.All(p => p.Value == Convert.ToHexString(replay.Sectors[p.Key])), "User-supplied raw macro sector and every other profile are unchanged.");
                var current = new MemorySnapshot { Description = snapshot.Description, Sectors = replay.Sectors };
                var binding = current.GetProfiles()[0].GetBinding(3);
                check("G502Hero-captured-binding-and-delays", ButtonBinding.MacroSector(binding) == 7 &&
                    Convert.ToHexString(MacroAllocator.ReadMacroBytes(current, new(7, ButtonBinding.MacroOffset(binding)))) == "430007400032440007FF",
                    "Actual failed draft: G4 executes D down, recorded/default 50ms, D up; stored in page 7 without changing native page 6.");
                await capture("G502Hero-native-macro-saved");
            }
            finally { vm.DetachDiagnosticReplay(); }
        }
        at = Array.IndexOf(args, "--g402-snapshot");
        if (at >= 0)
        {
            if (at + 1 >= args.Length) throw new ArgumentException("需要 G402 离线快照路径。");
            var snapshot = await OnboardMemory.LoadBackupAsync(args[at + 1]);
            vm.LoadLegacyDiagnosticSnapshot(snapshot); tabs.SelectedIndex = 0;
            var before = (byte[])snapshot.Sectors[1].Clone();
            var replay = vm.AttachDiagnosticReplay();
            try
            {
                vm.MacroTarget = vm.StandardBindings[5]; vm.BindMacroCommand.Execute(null);
                await vm.FlushImmediateSaveAsync();
                check("G402-captured-DPI-does-not-block-macro", !vm.HasError && replay.WriteStarts == 2 && vm.PersistenceStatus.Contains("读回一致"), vm.Status);
                check("G402-unchanged-DPI-and-fields-preserved", Enumerable.Range(0, before.Length).Where(i => i is < 52 or >= 56 and < 1022)
                    .All(i => before[i] == replay.Sectors[1][i]), "Only the requested G6 binding and profile CRC change; native 400/800/1600/3200 DPI are retained.");
                var current = new MemorySnapshot { Description = snapshot.Description, Sectors = replay.Sectors };
                var binding = current.GetProfiles()[0].GetBinding(5);
                check("G402-captured-macro-readback", ButtonBinding.MacroSector(binding) == 2 && Convert.ToHexString(MacroAllocator.ReadMacroBytes(current,
                    new(2, ButtonBinding.MacroOffset(binding)))) == "43002C40002844002C43005040007D44005002FF", "Space/Left macro retains the recorded 40ms and 125ms delays and while-held repeat.");
                await capture("G402-native-macro-saved");
            }
            finally { vm.DetachDiagnosticReplay(); }
        }
        vm.LoadBindingLabelsVerificationFixture(); tabs.SelectedIndex = 0;
        check("binding-labels-media-and-no-action", vm.StandardBindings[3].DisplayAction == "音量减少" && vm.StandardBindings[4].DisplayAction == "音量增加"
            && vm.StandardBindings.Skip(5).Take(4).All(b => b.DisplayAction == "禁用"), "Consumer EA/E9 and special NO_ACTION have user-facing names.");
        check("binding-labels-preserve-records", Convert.ToHexString(vm.StandardBindings[3].BuildBinding()) == "800300EA"
            && Convert.ToHexString(vm.StandardBindings[5].BuildBinding()) == "9000FFFF", "Presentation changes do not rewrite the loaded records.");
        await capture("G502Hero-friendly-bindings");
        vm.StandardBindings[3].SelectedAction = ButtonBindingEditor.ActionChoices.Single(c => c.Id == "consumer");
        vm.StandardBindings[3].Value = "0xE9";
        check("binding-labels-edited-media", vm.StandardBindings[3].DisplayAction == "音量增加", "Manually edited consumer values also render a name.");
    }
}
