using System.Runtime.InteropServices.WindowsRuntime;
using LogiBoardToolBox.Core.Diagnostics;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace LogiBoardToolBox.Services;

internal static class DiagnosticScreenshot
{
    public static async Task<IReadOnlyList<string>> CaptureAsync()
    {
        try
        {
            var session = DiagnosticLog.Session ?? throw new IOException("日志会话不可用。");
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(App.MainWindow.Content);
            var pixels = await bitmap.GetPixelsAsync();
            if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0 || pixels.Length == 0)
                throw new IOException("软件未返回有效的界面像素。");
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
            await encoder.FlushAsync();
            using var reader = new DataReader(stream.GetInputStreamAt(0)); await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
            Directory.CreateDirectory(session.DirectoryPath);
            var path = Path.Combine(session.DirectoryPath, $"ui-{DateTime.Now:yyyyMMdd-HHmmss-fffffff}.png");
            await File.WriteAllBytesAsync(path, bytes);
            var controls = Tree(App.MainWindow.Content).OfType<FrameworkElement>().Select(e => new
            {
                e.Name, Type = e.GetType().Name, AutomationId = Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(e),
                e.IsLoaded, Visibility = e.Visibility.ToString(), e.ActualWidth, e.ActualHeight,
                Focus = e is Control c ? c.FocusState.ToString() : null,
                Text = e switch { TextBox box => box.Text, TextBlock text => text.Text, ComboBox combo => combo.SelectedItem?.ToString(),
                    ToggleSwitch toggle => toggle.IsOn.ToString(), CheckBox check => check.IsChecked?.ToString(), Button button => button.Content as string, _ => null },
                Committed = e is Controls.CommitTextBox field ? field.Value : null,
                Error = e is Controls.CommitTextBox input ? input.ErrorText : null
            }).ToArray();
            await File.WriteAllTextAsync(Path.ChangeExtension(path, ".json"), Core.Diagnostics.DiagnosticJson.Serialize(new { Capture = "App visual tree", Controls = controls }));
            DiagnosticLog.Record("diagnostics", "app visual tree captured", new { path, bitmap.PixelWidth, bitmap.PixelHeight, DesktopCapture = false });
            return [];
        }
        catch (Exception error)
        {
            DiagnosticLog.Record("diagnostic-error", "app screenshot unavailable", error: error);
            return ["软件界面截图不可用: " + error.Message];
        }
    }
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
