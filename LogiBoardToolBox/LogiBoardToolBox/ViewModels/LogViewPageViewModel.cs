using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.Storage.Pickers;
using WinRT.Interop;
using XFEExtension.NetCore.FileExtension;
using XFEExtension.NetCore.WinUIHelper.Implements;
using XFEExtension.NetCore.WinUIHelper.Interface;
using XFEExtension.NetCore.XFEConsole;

namespace LogiBoardToolBox.ViewModels
{
    public partial class LogViewPageViewModel : SearchableViewModelBase<XFELogEntry, string>, IRefreshableViewModel // Or use IAsyncRefreshableViewModel if you want to make Refresh asynchronous
    {
        [ObservableProperty] public partial bool IsStartDateTimeEnable { get; set; } = true;
        [ObservableProperty] public partial bool IsEndDateTimeEnable { get; set; } = true;
        [ObservableProperty] public partial DateTime StartDateTime { get; set; } = DateTime.Today.AddDays(-7);
        [ObservableProperty] public partial DateTime EndDateTime { get; set; } = DateTime.Today.AddDays(1).AddSeconds(-1);

        public LogViewPageViewModel()
        {
            SearchPredicate = (searchText, log) => log.ToString().Contains(searchText);
            AutoNavigationParameterService.ParameterChange += AutoNavigationParameterService_ParameterChange;
        }

        private void AutoNavigationParameterService_ParameterChange(object? sender, string? e)
        {
            Console.WriteLine($"切换至日志页面，参数: {e}");
            Refresh();
        }

        [RelayCommand]
        public void Refresh()
        {
            var logs = XFEConsole.Log.Logs;
            if (IsStartDateTimeEnable)
                logs = [.. logs.Where(log => log.Time >= StartDateTime)];
            if (IsEndDateTimeEnable)
                logs = [.. logs.Where(log => log.Time <= EndDateTime)];
            ModelList = logs;
            Search();
            Console.WriteLine($"日志已刷新，共 {ModelList.Count} 条记录。");
        }

        [RelayCommand]
        private async Task ExportLog()
        {
            try
            {
                if (Utilities.UiVerification.IsRequested) throw new InvalidOperationException("后台验收不打开系统文件对话框。");
                var savePicker = new FileSavePicker();
                InitializeWithWindow.Initialize(savePicker, WindowNative.GetWindowHandle(App.MainWindow));
                savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                savePicker.FileTypeChoices.Add(new("完整诊断包", [".zip"]));
                savePicker.SuggestedFileName = $"LogiBoard-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}";
                savePicker.DefaultFileExtension = ".zip";
                if (await savePicker.PickSaveFileAsync() is not { } file) return;
                var captureIssues = await Services.DiagnosticScreenshot.CaptureAsync();
                await App.MouseWorkspace.ExportDiagnosticsAsync(file.Path, captureIssues);
                Console.WriteLine($"完整诊断包已导出至: {file.Path}");
            }
            catch (Exception error) { App.MouseWorkspace.ShowError(error); }
        }

        [RelayCommand]
        private void ClearLog()
        {
            XFEConsole.Log.Clear();
            Refresh();
            Console.WriteLine("日志已清空。");
        }
    }
}
