using System.Buffers.Binary;
using System.Text.Json;
using LogiBoardToolBox.Controls;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Profiles;
using LogiBoardToolBox.Views;

namespace LogiBoardToolBox.Utilities;

internal static partial class UiVerification
{
    private static async Task VerifyG304XDpiSaveAsync(MainPage page, WorkspaceHost tabs, Func<string, Task> capture,
        Action<string, bool, string> check)
    {
        var snapshot = await OnboardMemory.LoadBackupAsync(RequiredPath("--g304x-snapshot"));
        var sensor = JsonSerializer.Deserialize(await File.ReadAllTextAsync(RequiredPath("--g304x-sensor")), ProfileJsonContext.Default.SensorPerformance)
            ?? throw new InvalidDataException("缺少传感器诊断数据。");
        var before = snapshot.Sectors[1];
        check("captured-enabled-stages-use-zero-lod", Enumerable.Range(0, 5).All(i => before[8 + 5 * i] == 0), "G304 X capture uses the zero LOD encoding.");

        var vm = page.ViewModel;
        vm.LoadAdvancedDpiDiagnosticSnapshot(snapshot, sensor, "G304X");
        var replay = vm.AttachDiagnosticReplay();
        try
        {
            tabs.SelectedIndex = 2;
            var first = vm.DpiStages[0];
            var last = vm.DpiStages[4];
            first.Value = "2600";
            last.Enabled = false;
            await vm.FlushImmediateSaveAsync();
            check("g304x-dpi-save-readback", !vm.HasError && replay.WriteStarts == 1 && vm.PersistenceStatus.Contains("读回一致"), vm.Status);

            var expected = (byte[])before.Clone();
            BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(4), 2600);
            BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(6), 2600);
            expected.AsSpan(24, 5).Clear();
            check("g304x-only-edited-dpi-and-crc-change", replay.Sectors[1].SequenceEqual(SectorChecksum.Seal(expected)), "The enabled LOD bytes remain 0, and the disabled triplet is cleared.");
            await capture("G304X-dpi-saved");

            last.Enabled = true;
            await vm.FlushImmediateSaveAsync();
            before.AsSpan(24, 5).CopyTo(expected.AsSpan(24));
            check("g304x-reenabled-stage-readback", !vm.HasError && replay.WriteStarts == 2 && replay.Sectors[1].SequenceEqual(SectorChecksum.Seal(expected)), vm.Status);
            check("g304x-other-sectors-preserved", snapshot.Sectors.Where(p => p.Key != 1).All(p => p.Value.SequenceEqual(replay.Sectors[p.Key])), "The original directory, other profiles and macro sectors match byte for byte.");
            var backup = await OnboardMemory.LoadBackupAsync(vm.LastBackupPath);
            check("g304x-backup-readable", backup.DeviceIdentity == snapshot.DeviceIdentity && backup.Sectors[1][28] == 0, "Automatic backup loads under the same executable.");
            await capture("G304X-dpi-reenabled");
        }
        finally { vm.DetachDiagnosticReplay(); }

        static string RequiredPath(string option)
        {
            var index = Array.IndexOf(args, option);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("缺少诊断文件参数 " + option);
        }
    }
}
