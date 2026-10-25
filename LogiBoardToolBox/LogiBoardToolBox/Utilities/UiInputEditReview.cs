using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text.Json;
using LogiBoardToolBox.Controls;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Core.Windows;
using LogiBoardToolBox.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace LogiBoardToolBox.Utilities;

internal static partial class UiVerification
{
    public static async Task RunInputEditReviewAsync(MainPage page, WorkspaceHost tabs)
    {
        var folder = OutputDirectory; Directory.CreateDirectory(folder);
        var checks = new List<object>(); var states = new List<object>(); var images = new List<object>();
        var vm = page.ViewModel; var device = vm.SelectedDevice ?? throw new InvalidOperationException("输入验证需要读取当前鼠标。");
        using var client = LogitechDiscovery.Open(device); var memory = new OnboardMemory(client, device.Identity, device.Name);
        var before = await memory.ReadAsync(); await OnboardMemory.SaveBackupAsync(before, Path.Combine(folder, "before.json"));
        GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundBefore);
        string? failure = null;
        try
        {
            if (HardwareWritesEnabled) throw new InvalidOperationException("输入验证必须禁止实机写入。");
            tabs.SelectedIndex = 2; await Task.Delay(350); page.UpdateLayout();
            var stage = vm.DpiStages[0]; var originalX = stage.Value; var originalY = stage.ValueY; var linked = stage.LinkAxes;
            var field = Field("stage-dpi-x", stage); var committedEvents = 0; field.ValueCommitted += CountCommit;
            foreach (var text in new[] { "", "1", "16", "160", "1600" })
            {
                field.Text = text; await Task.Delay(350);
                Check("draft-" + (text.Length == 0 ? "empty" : text), stage.Value == originalX && stage.ValueY == originalY && field.Value == originalX && field.ErrorText == "" && vm.DpiInputError == "" && !vm.HasError && !vm.IsBusy && committedEvents == 0, "Draft stayed local beyond the former 200ms save debounce; no validation or commit event.");
                State("draft-" + text, field, stage);
                if (text == "1") await Capture("01-typing-1");
                if (text == "1600") await Capture("02-typing-1600");
            }
            Check("enter-commits-complete-value", field.Commit(TextCommitReason.Enter) && stage.Value == "1600" && committedEvents == 1, "The same commit handler used by Enter updates the TwoWay setting binding once.");
            Check("blur-after-enter-does-not-commit-again", field.Commit(TextCommitReason.LostFocus) && committedEvents == 1, "A subsequent blur cannot queue another change.");
            await Capture("03-committed-1600");
            field.Text = "1"; await Task.Delay(350);
            Check("invalid-only-validated-at-commit", field.ErrorText == "" && !field.Commit(TextCommitReason.Enter) && stage.Value == "1600" && committedEvents == 1 && vm.DpiInputError.Length != 0 && !vm.HasError, "Rejected at explicit commit; the last valid setting is retained and no persistence failure is shown.");
            State("rejected-enter-1", field, stage); await Capture("04-rejected-enter-1");
            field.Text = "16"; await Task.Delay(100);
            Check("editing-clears-local-error", field.ErrorText == "" && vm.DpiInputError == "", "Editing resumes without repeating validation.");
            field.CancelDraft(); Check("escape-cancels-draft", field.Text == "1600" && stage.Value == "1600" && committedEvents == 1, "The Escape handler restores the last committed text without a save.");
            field.ValueCommitted -= CountCommit;
            stage.Value = originalX; stage.ValueY = originalY;
            vm.AdvancedDpi = true; stage.LinkAxes = false; await Task.Delay(200); page.UpdateLayout();
            var yField = Field("stage-dpi-y", stage); yField.Text = "1"; await Task.Delay(350);
            Check("y-axis-draft-isolated", stage.ValueY == originalY && stage.Value == originalX && yField.ErrorText == "", "Independent Y input also remains a local draft.");
            yField.Text = "2000"; Check("y-axis-blur-commits", yField.Commit(TextCommitReason.LostFocus) && stage.ValueY == "2000" && stage.Value == originalX, "Blur commits only the edited Y axis.");
            stage.ValueY = originalY; stage.LinkAxes = linked; vm.AdvancedDpi = false;

            tabs.SelectedIndex = 4; await Task.Delay(200); page.UpdateLayout();
            var power = Field("power-save-input"); var originalPower = vm.PowerSaveSeconds;
            power.Text = "6"; await Task.Delay(350); Check("timeout-draft-isolated", vm.PowerSaveSeconds == originalPower && power.ErrorText == "", "Timeout typing does not update persisted settings.");
            power.Text = "90"; Check("timeout-enter-commit", power.Commit() && vm.PowerSaveSeconds == "90", "A complete valid timeout is committed on Enter."); vm.PowerSaveSeconds = originalPower;
            power.Text = "bad"; Check("timeout-invalid-keeps-value", !power.Commit() && vm.PowerSaveSeconds == originalPower, "Invalid timeout does not enter the settings model."); power.CancelDraft();
            var expander = Tree(page).OfType<Microsoft.UI.Xaml.Controls.Expander>().First(e => e.Header as string == "板载写入与备份");
            expander.IsExpanded = true; await Task.Delay(100); page.UpdateLayout();
            var name = Field("profile-name-input"); var originalName = vm.ProfileName;
            name.Text = "输入草稿"; await Task.Delay(350); Check("profile-name-draft-isolated", vm.ProfileName == originalName, "Profile names use the same explicit commit boundary."); name.CancelDraft();
            tabs.SelectedIndex = 3; var effect = vm.SelectedLightsyncEffect; vm.SelectedLightsyncEffect = vm.LightsyncEffects.Single(e => e.Id == "Fixed"); await Task.Delay(200); page.UpdateLayout();
            var color = Field("lightsync-color-input"); var originalColor = vm.LightsyncColor;
            color.Text = "#1"; await Task.Delay(350); Check("color-draft-isolated", vm.LightsyncColor == originalColor && color.ErrorText == "", "Incomplete hex color remains local.");
            Check("invalid-color-does-not-update-settings", !color.Commit() && vm.LightsyncColor == originalColor, "Invalid hex color cannot stage an onboard lighting write."); color.CancelDraft(); vm.SelectedLightsyncEffect = effect;
            tabs.SelectedIndex = 2;
            void CountCommit(object? sender, EventArgs e) => committedEvents++;
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            var after = await memory.ReadAsync(); await OnboardMemory.SaveBackupAsync(after, Path.Combine(folder, "after.json"));
            var unchanged = before.Mode == after.Mode && before.ActiveSector == after.ActiveSector && before.Sectors.All(p => p.Value.SequenceEqual(after.Sectors[p.Key]));
            checks.Add(new { Name = "real-onboard-state-unchanged", Passed = unchanged, Detail = "All original sectors, mode and active profile match; no real DPI setting was applied by this verification." });
            if (!unchanged) failure ??= "The real onboard state changed during verification.";
            GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundAfter);
            await File.WriteAllTextAsync(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new
            {
                ProcessId = Environment.ProcessId, Failure = failure, Checks = checks, States = states, Images = images,
                ForegroundBefore = foregroundBefore, ForegroundAfter = foregroundAfter, HardwareWritesEnabled,
                BuildSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(UiVerification).Assembly.Location))),
                InputMethod = "Direct control text plus the exact Enter/blur/Escape commit handlers; no OS input injection or window activation",
                SoftwareEnginesStopped = !vm.HostMacrosRunning && !vm.LightsyncRunning
            }, new JsonSerializerOptions { WriteIndented = true }));
            App.MainWindow.Close();
        }
        CommitTextBox Field(string id, object? tag = null) => Tree(page).OfType<CommitTextBox>().Single(f => AutomationProperties.GetAutomationId(f) == id && (tag is null || ReferenceEquals(f.Tag, tag)));
        void Check(string name, bool passed, string detail) { checks.Add(new { Name = name, Passed = passed, Detail = detail }); if (!passed) throw new InvalidOperationException(name + ": " + detail); }
        void State(string name, CommitTextBox field, ViewModels.DpiStageEditor stage) => states.Add(new { Name = name, field.Text, field.Value, ModelX = stage.Value, ModelY = stage.ValueY, field.ErrorText, vm.DpiInputError, vm.HasError, vm.IsBusy });
        async Task Capture(string name)
        {
            var root = (FrameworkElement)App.MainWindow.Content;
            foreach (var element in Tree(root).OfType<FrameworkElement>()) { element.InvalidateMeasure(); element.InvalidateArrange(); }
            root.UpdateLayout(); await Task.Delay(500);
            var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(root); var pixels = await bitmap.GetPixelsAsync();
            using var stream = new InMemoryRandomAccessStream(); var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray()); await encoder.FlushAsync();
            using var reader = new DataReader(stream.GetInputStreamAt(0)); await reader.LoadAsync((uint)stream.Size); var bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
            var path = Path.Combine(folder, name + ".png"); await File.WriteAllBytesAsync(path, bytes);
            GetWindowThreadProcessId(GetForegroundWindow(), out var foreground);
            images.Add(new { Path = path, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Width = bitmap.PixelWidth, Height = bitmap.PixelHeight, ForegroundProcess = foreground });
        }
    }
}
