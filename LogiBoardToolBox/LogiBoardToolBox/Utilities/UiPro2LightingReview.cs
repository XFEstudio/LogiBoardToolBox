using System.Buffers.Binary;
using System.Text.Json;
using LogiBoardToolBox.Controls;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Profiles;
using LogiBoardToolBox.Views;
using Microsoft.UI.Xaml.Automation;

namespace LogiBoardToolBox.Utilities;

internal static partial class UiVerification
{
    private static async Task VerifyPro2LightingAsync(MainPage page, WorkspaceHost tabs, Func<string, Task> capture,
        Action<string, bool, string> check)
    {
        var index = Array.IndexOf(args, "--pro2-lighting-replay") + 1;
        if (index >= args.Length) throw new ArgumentException("需要 PRO 2 诊断回放目录。");
        var folder = args[index];
        var snapshot = await OnboardMemory.LoadBackupAsync(Path.Combine(folder, "snapshot.json"));
        var sensor = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(folder, "sensor.json")), ProfileJsonContext.Default.SensorPerformance)
            ?? throw new InvalidDataException("缺少传感器诊断数据。");
        var info = (await File.ReadAllLinesAsync(Path.Combine(folder, "rgb-info.txt"))).Select(Convert.FromHexString)
            .ToDictionary(p => BinaryPrimitives.ReadUInt16BigEndian(p), p => p[2..]);

        var vm = page.ViewModel;
        vm.LoadAdvancedDpiDiagnosticSnapshot(snapshot, sensor, "Pro2");
        var replay = await vm.AttachLightingDiagnosticReplayAsync(info);
        var original = snapshot.Sectors[2];
        var expected = (byte[])original.Clone();
        try
        {
            tabs.SelectedIndex = 3;
            check("pro2-real-one-cluster-in-board-2", vm.SelectedConfiguration?.Key == "board-2" && vm.LedZones.Count == 1 &&
                vm.CanEditLighting && vm.SelectedLightsyncEffect.Id == "Fixed" && vm.LightsyncColor == "#B6E5F4",
                "Captured format-7 slot and 0x8071 replies enable fixed lighting on the single reported cluster.");
            await capture("PRO2-lightsync-before");
            check("pro2-opening-lighting-does-not-write", replay.WriteStarts == 0, "Reading and opening LIGHTSYNC are read-only.");

            vm.LightsyncBrightness = 57;
            await VerifyRecord("pro2-brightness-autosaves", "0167828B02000000000000", 1);

            var field = Tree(page).OfType<CommitTextBox>().Single(e => AutomationProperties.GetAutomationId(e) == "lightsync-color-input");
            field.Text = "#24C060";
            check("pro2-color-edit-waits-for-commit", replay.WriteStarts == 1 && vm.LightsyncColor == "#B6E5F4", "A draft color leaves the current board value intact.");
            check("pro2-color-control-commits", field.Commit(), field.ErrorText);
            await VerifyRecord("pro2-color-autosaves", "01146D3602000000000000", 2);

            var expander = Tree(page).OfType<Expander>().Single(e => e.Header as string == "设备与板载");
            expander.IsExpanded = true;
            await capture("PRO2-lightsync-saved");
            var softwareApply = Tree(expander).OfType<Button>().Single(b => b.Content as string == "应用到鼠标");
            var stage = Tree(expander).OfType<Button>().Single(b => b.Content as string == "加入板载草稿");
            check("pro2-board-shows-saved-without-software-apply", softwareApply.Visibility == Visibility.Collapsed &&
                stage.IsEnabled && !Tree(page).OfType<InfoBar>().Single(b => b.Name == "StatusBar").IsOpen &&
                vm.LightsyncStorageNote.Contains("空闲灯效保持原值"), vm.PersistenceStatus);

            vm.LightsyncPeriod = 3000;
            SelectEffect("Breathing");
            await VerifyRecord("pro2-breathing-uses-effect-id", "0A24C0600BB80139000000", 3);
            SelectEffect("Cycle");
            await VerifyRecord("pro2-cycle-autosaves", "0300000000000BB8390000", 4);
            SelectEffect("Off");
            await VerifyRecord("pro2-off-autosaves", "0000000000000000000000", 5);

            SelectEffect("Ripple");
            await vm.FlushImmediateSaveAsync();
            check("pro2-software-effect-leaves-board-intact", !vm.HasError && !stage.IsEnabled && replay.WriteStarts == 5 &&
                vm.PersistenceStatus.Contains("需软件配置") && replay.Sectors[2].SequenceEqual(expected), vm.PersistenceStatus);
            await capture("PRO2-lightsync-software-only");
            vm.PowerSaveSeconds = (BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(44)) == 60 ? 61 : 60).ToString();
            BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(44), ushort.Parse(vm.PowerSaveSeconds));
            await vm.FlushImmediateSaveAsync();
            check("pro2-unsupported-lighting-does-not-block-other-saves", !vm.HasError && replay.WriteStarts == 6 &&
                replay.Sectors[2].SequenceEqual(SectorChecksum.Seal(expected)), vm.Status);
            check("pro2-other-sectors-unchanged", snapshot.Sectors.Where(p => p.Key != 2).All(p => replay.Sectors[p.Key].SequenceEqual(p.Value)),
                "Directory, other user/factory profiles, and macro sectors match the original capture byte for byte.");
            var backup = await OnboardMemory.LoadBackupAsync(vm.LastBackupPath);
            check("pro2-backup-readable", backup.ActiveSector == 2 && SectorChecksum.IsValid(backup.Sectors[2]),
                "Backup generated by the normal save flow reloads successfully.");

            await vm.SelectConfigurationAsync(vm.Configurations.Single(c => c.IsSoftware));
            check("pro2-software-apply-still-available", !vm.HasError && softwareApply.Visibility == Visibility.Visible && softwareApply.IsEnabled,
                "The software profile retains the normal LIGHTSYNC apply action.");
        }
        finally { vm.DetachDiagnosticReplay(); }

        void SelectEffect(string effect) => vm.SelectedLightsyncEffect = vm.LightsyncEffects.Single(e => e.Id == effect);

        async Task VerifyRecord(string name, string hex, int writes)
        {
            await vm.FlushImmediateSaveAsync();
            Convert.FromHexString(hex).CopyTo(expected, 208);
            check(name, !vm.HasError && replay.WriteStarts == writes && vm.PersistenceStatus.Contains("读回一致") &&
                replay.Sectors[2].SequenceEqual(SectorChecksum.Seal(expected)),
                vm.PersistenceStatus + " / " + Convert.ToHexString(replay.Sectors[2].AsSpan(208, 11)));
        }
    }
}
