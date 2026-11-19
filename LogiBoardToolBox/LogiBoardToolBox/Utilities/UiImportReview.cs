using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text.Json;
using LogiBoardToolBox.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace LogiBoardToolBox.Utilities;

internal static partial class UiVerification
{
    /// <summary>Exports the real imported workspace without injecting input or editing device settings.</summary>
    public static async Task RunImportReviewAsync(MainPage page)
    {
        var index = Array.IndexOf(args, "--import-review");
        if (index < 0) return;
        if (index + 1 >= args.Length) throw new ArgumentException("需要导入核对输出目录。");
        var folder = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(folder);
        var vm = page.ViewModel; var side = vm.IsSideView;
        GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundBefore);
        var images = new List<object>();
        try
        {
            vm.IsSideView = false; await Capture("assignments-front");
            vm.IsSideView = true; await Capture("assignments-side");
        }
        finally { vm.IsSideView = side; }
        GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundAfter);
        await File.WriteAllTextAsync(Path.Combine(folder, "workspace.json"), Core.Diagnostics.DiagnosticJson.Serialize(new
        {
            ProcessId = Environment.ProcessId, BuildSha256 = await GetBuildHashAsync(),
            vm.HasError, vm.Status, vm.ModeSummary, Configuration = vm.SelectedConfiguration?.Label,
            ForegroundBefore = foregroundBefore, ForegroundAfter = foregroundAfter, Images = images,
            Bindings = vm.AllBindings.Where(b => b.SelectedAction.Id == "macro").Select(b => new { b.Index, b.GShift, b.Label, b.Value, b.DisplayAction, Original = Convert.ToHexString(b.Original) }),
            Palette = vm.AssignmentItems.Select(i => new { i.Name, i.AssignedTo, i.Description }),
            Macros = vm.Macros.Select(m => new { m.Name, m.Script, Repeat = m.Repeat.ToString(), m.UseStandardDelay, m.StandardDelay, m.HoldScript, m.ReleaseScript }),
            HostMacroEngine = vm.HostMacrosRunning, SoftwareLighting = vm.LightsyncRunning
        }, indented: true));

        async Task Capture(string name)
        {
            var root = (FrameworkElement)App.MainWindow.Content;
            foreach (var element in Tree(root).OfType<FrameworkElement>()) { element.InvalidateMeasure(); element.InvalidateArrange(); }
            root.UpdateLayout(); await Task.Delay(500);
            var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(root);
            var buffer = await bitmap.GetPixelsAsync();
            if (bitmap.PixelWidth < 500 || bitmap.PixelHeight < 400 || buffer.Length == 0) throw new InvalidOperationException("导入核对截图尺寸无效。");
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, buffer.ToArray());
            await encoder.FlushAsync(); using var reader = new DataReader(stream.GetInputStreamAt(0)); await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
            var path = Path.Combine(folder, name + ".png"); await File.WriteAllBytesAsync(path, bytes);
            GetWindowThreadProcessId(GetForegroundWindow(), out var foreground);
            images.Add(new { Path = path, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Width = bitmap.PixelWidth, Height = bitmap.PixelHeight, SideView = vm.IsSideView, ForegroundProcess = foreground });
        }
    }
}
