using Windows.Storage.Pickers;

namespace LogiBoardToolBox.Views;

public sealed partial class MainPage
{
    internal string? VerificationDiagnosticExportPath { get; set; }
    private bool choosingDiagnosticPath;
    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (choosingDiagnosticPath || ViewModel.ExportingDiagnostics) return;
        choosingDiagnosticPath = true;
        try
        {
            string? path;
            if (Utilities.UiVerification.IsRequested)
            {
                path = VerificationDiagnosticExportPath ?? throw new InvalidOperationException("后台验收只能导出到隔离目录。");
                var root = Path.GetFullPath(Utilities.UiVerification.OutputDirectory) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("后台诊断导出不能写入隔离目录之外。");
            }
            else
            {
                var picker = new FileSavePicker { SuggestedFileName = $"LogiBoard-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}", SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeChoices.Add("完整诊断包", new List<string> { ".zip" });
                InitializePicker(picker);
                path = (await picker.PickSaveFileAsync())?.Path;
            }
            if (path is not null)
            {
                var captureIssues = await Services.DiagnosticScreenshot.CaptureAsync();
                await ViewModel.ExportDiagnosticsAsync(path, captureIssues);
            }
        }
        catch (Exception error) { ViewModel.ShowError(error); }
        finally { choosingDiagnosticPath = false; }
    }

    private async void RetrySave_Click(object sender, RoutedEventArgs e) => await ViewModel.RetryOnboardSaveAsync();
}
