using LogiBoardToolBox.Controls;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Views;

namespace LogiBoardToolBox.Utilities;

internal static partial class UiVerification
{
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
