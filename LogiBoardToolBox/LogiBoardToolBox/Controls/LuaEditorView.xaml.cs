using LogiBoardToolBox.ViewModels;
using Windows.Storage.Pickers;

namespace LogiBoardToolBox.Controls;

public sealed partial class LuaEditorView : UserControl
{
    public MainPageViewModel ViewModel => App.MouseWorkspace;
    public event EventHandler? Closed;
    public LuaEditorView() => InitializeComponent();
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ConvertLuaToBoardCancelCommand.Execute(null);
        try { ViewModel.CommitLuaEditor(); Closed?.Invoke(this, EventArgs.Empty); }
        catch (Exception error) { ViewModel.LuaStatus = error.Message; }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.CommitLuaEditor();
            var path = await Services.LuaFileExport.SaveAsync(ViewModel.LuaName, ViewModel.LuaSource);
            if (path is not null) ViewModel.LuaStatus = "Lua 原文已导出：" + path;
        }
        catch (Exception error) { ViewModel.LuaStatus = "导出失败：" + error.Message; Core.Diagnostics.DiagnosticLog.Record("lua-export", "failed", error: error); }
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".lua");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            ViewModel.LuaStatus = "正在读取 Lua 文件…";
            var imported = await Core.Macros.LuaSourceFile.ReadAsync(file.Path);
            await ViewModel.StopLuaCommand.ExecuteAsync(null);
            ViewModel.LuaSource = imported.Source; ViewModel.LuaName = imported.Name;
            ViewModel.LuaStatus = "Lua 已导入。可选择左右键执行并启用，或检查板载转换。";
        }
        catch (Exception error) { ViewModel.LuaStatus = "导入失败：" + error.Message; Core.Diagnostics.DiagnosticLog.Record("lua-import", "failed", error: error); }
    }
}
