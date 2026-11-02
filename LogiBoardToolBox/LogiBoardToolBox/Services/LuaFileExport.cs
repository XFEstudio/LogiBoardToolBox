using LogiBoardToolBox.Core.Macros;
using Windows.Storage.Pickers;

namespace LogiBoardToolBox.Services;

internal static class LuaFileExport
{
    public static async Task<string?> SaveAsync(string name, string source)
    {
        if (Utilities.UiVerification.IsRequested) throw new InvalidOperationException("后台验收使用隔离文件导出，不打开系统保存对话框。");
        var invalid = Path.GetInvalidFileNameChars();
        var suggested = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (suggested.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) suggested = suggested[..^4];
        if (string.IsNullOrWhiteSpace(suggested)) suggested = "macro";
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = suggested.Length > 100 ? suggested[..100] : suggested };
        picker.FileTypeChoices.Add("Lua 脚本", new List<string> { ".lua" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return null;
        await LuaSourceFile.WriteAsync(file.Path, source);
        return file.Path;
    }
}
