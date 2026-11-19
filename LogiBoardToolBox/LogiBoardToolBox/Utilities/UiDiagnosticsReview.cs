using System.IO.Compression;
using System.Text.Json;
using LogiBoardToolBox.Core.Devices;
using LogiBoardToolBox.Controls;
using LogiBoardToolBox.Views;
using Microsoft.UI.Xaml.Controls;

namespace LogiBoardToolBox.Utilities;

internal static partial class UiVerification
{
    private static async Task VerifyDiagnosticsAsync(MainPage page, WorkspaceHost tabs, Func<string, Task> capture, Action<string, bool, string> check)
    {
        var vm = page.ViewModel;
        vm.LoadCatalogVerificationFixture(MouseCatalog.ById("G502Hero")!); tabs.SelectedIndex = 0;
        var validBackup = vm.CaptureDiagnosticContext().GetProperty("Snapshot").Deserialize(Profiles.ProfileJsonContext.Default.MemorySnapshot)!;
        vm.LoadDiagnosticFailureFixture();
        check("diagnostics-retry-enabled-for-onboard-draft", vm.CanRetryOnboardSave, "A failed onboard save can be retried without changing configuration or losing the draft.");
        check("diagnostics-error-has-sector-crc-context", vm.HasError && vm.Status.Contains("扇区 6") && vm.Status.Contains("CRC 不一致"), vm.Status);
        await capture("Diagnostics-sector-error");
        check("diagnostics-export-available-with-error", Tree(page).OfType<Button>().Any(b =>
            Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(b) == "error-export-diagnostics" && b.IsEnabled && b.ActualWidth > 0), "A failed save exposes diagnostic export directly.");
        var errorPackage = Path.Combine(OutputDirectory, "error-diagnostics.zip");
        page.VerificationDiagnosticExportPath = errorPackage;
        Invoke(page, "error-export-diagnostics");
        await Until(() => File.Exists(errorPackage) && !vm.ExportingDiagnostics, "error-diagnostics-export");
        using (var zip = ZipFile.OpenRead(errorPackage))
        {
            using var context = JsonDocument.Parse(await Read("context.json"));
            var data = context.RootElement;
            check("diagnostics-raw-snapshot-and-draft-exported", data.GetProperty("Snapshot").GetProperty("Sectors").TryGetProperty("6", out _)
                && data.GetProperty("Macros").EnumerateArray().Any(m => m.GetProperty("Name").GetString() == "测试")
                && data.GetProperty("Draft").GetProperty("Bindings").EnumerateArray().Any(b => b.GetProperty("Value").GetString() == "测试")
                && data.GetProperty("SelectedDevice").GetProperty("Name").GetString() == MouseCatalog.ById("G502Hero")!.DisplayName,
                "Exports the actual failing simulated sector, exact device identity and unsaved macro/binding draft.");
            using var manifest = JsonDocument.Parse(await Read("manifest.json"));
            check("diagnostics-export-is-complete", manifest.RootElement.GetProperty("Issues").GetArrayLength() == 0 && zip.Entries.Any(e => e.FullName.StartsWith("sessions/")), "Manifest reports no missing files; journal is included.");
            check("diagnostics-app-screenshot-is-included", zip.Entries.Any(e => e.FullName.StartsWith("sessions/") && e.FullName.EndsWith(".png")), "Exports the application's own RenderTargetBitmap; no desktop pixels or other windows are captured.");
            var journals = string.Join("\n", await Task.WhenAll(zip.Entries.Where(e => e.FullName.EndsWith(".jsonl")).Select(e => Read(e.FullName))));
            check("diagnostics-full-exception-saved", journals.Contains("MacroStorageException") && journals.Contains("MacroAllocator.ReadStorage") && journals.Contains("00060000") && journals.Contains("ProfileSector"), "Full thrown stack, original binding and profile context survive ZIP export.");
            async Task<string> Read(string name) { using var reader = new StreamReader(zip.GetEntry(name)!.Open()); return await reader.ReadToEndAsync(); }
        }
        check("diagnostics-export-keeps-failed-edit", vm.HasError && vm.Macros.Any(m => m.Name == "测试") && vm.StandardBindings[5].Value == "测试", "Export neither clears the error nor discards the pending assignment.");
        vm.HasError = false; tabs.SelectedIndex = 4; page.UpdateLayout();
        var settings = (ScrollViewer)page.FindName("SettingsScroll");
        await Task.Delay(150); settings.ChangeView(null, settings.ScrollableHeight, null, true);
        await capture("Diagnostics-settings-export");
        check("diagnostics-settings-export-control", Tree(page).OfType<Button>().Any(b => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(b) == "diagnostics-export" && b.ActualWidth > 0 && b.IsEnabled), "Settings exposes an export control even without a connected mouse.");
        page.VerificationDiagnosticExportPath = Path.Combine(OutputDirectory, "settings-diagnostics.zip");
        Invoke(page, "diagnostics-export");
        await Until(() => File.Exists(page.VerificationDiagnosticExportPath) && !vm.ExportingDiagnostics, "settings-diagnostics-export");
        check("diagnostics-export-from-settings", vm.DiagnosticsStatus.StartsWith("诊断包已导出") && !vm.DiagnosticsStatus.Contains("缺失"), vm.DiagnosticsStatus);
        vm.ProfileName = "恢复后保留编辑";
        var replay = vm.AttachDiagnosticReplay();
        try
        {
            await vm.RetryOnboardSaveAsync();
            check("diagnostics-failed-retry-keeps-drafts", vm.HasError && replay.WriteStarts == 0 && vm.ProfileName == "恢复后保留编辑" && vm.StandardBindings[5].Value == "测试", "Persistent invalid CRC cannot write flash; the name and macro assignment remain editable.");
            await Core.Onboard.OnboardMemory.SaveBackupAsync(validBackup, Path.Combine(vm.BackupDirectory, "known-good-before-error.json"));
            await vm.RetryOnboardSaveAsync();
            check("diagnostics-retry-recovers-save", !vm.HasError && replay.WriteStarts > 0 && vm.PersistenceStatus.Contains("读回一致") && vm.ProfileName == "恢复后保留编辑", vm.PersistenceStatus);
            var stored = new Core.Onboard.MouseProfile(1, true, replay.Sectors[1], profileFormat: 3);
            check("diagnostics-retry-preserves-and-saves-macro", stored.Name == "恢复后保留编辑" && Core.Onboard.ButtonBinding.IsMacro(stored.GetBinding(5)), "The actual Retry path reads fresh memory, commits the pending draft and verifies the simulated firmware bytes.");
            check("diagnostics-retry-restores-verified-crc", replay.Sectors[6].SequenceEqual(validBackup.Sectors[6]), "The device still had a bad CRC when Retry began; an exact payload match to the saved valid backup repaired it as part of the verified commit.");
            page.VerificationDiagnosticExportPath = Path.Combine(OutputDirectory, "recovered-diagnostics.zip");
            Invoke(page, "diagnostics-export");
            await Until(() => File.Exists(page.VerificationDiagnosticExportPath) && !vm.ExportingDiagnostics, "recovered-diagnostics-export");
            using var recovered = ZipFile.OpenRead(page.VerificationDiagnosticExportPath);
            var lines = new List<JsonElement>();
            foreach (var entry in recovered.Entries.Where(e => e.FullName.EndsWith(".jsonl")))
            {
                using var reader = new StreamReader(entry.Open());
                foreach (var line in (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                { using var record = JsonDocument.Parse(line); lines.Add(record.RootElement.Clone()); }
            }
            check("diagnostics-hid-trace-is-exported", lines.Any(l => l.GetProperty("Category").GetString() == "hid" && l.GetProperty("Message").GetString() == "TX")
                && lines.Any(l => l.GetProperty("Category").GetString() == "hid" && l.GetProperty("Message").GetString() == "RX")
                && lines.Any(l => l.GetProperty("Category").GetString() == "memory-write" && l.GetProperty("Message").GetString() == "sector verified"),
                "Real HID++ client request/reply and OnboardMemory commit code run against the in-memory transport; all evidence is exported without a physical handle.");
        }
        finally { vm.DetachDiagnosticReplay(); }
        settings.ChangeView(null, 0, null, true); page.VerificationDiagnosticExportPath = null;
        vm.LoadCatalogVerificationFixture(MouseCatalog.ById("GPW")!); tabs.SelectedIndex = 0;
    }
}
