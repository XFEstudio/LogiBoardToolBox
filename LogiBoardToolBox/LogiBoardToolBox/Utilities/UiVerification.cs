using System.Text.Json;
using System.Runtime.InteropServices.WindowsRuntime;
using LogiBoardToolBox.ViewModels;
using LogiBoardToolBox.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace LogiBoardToolBox.Utilities;

/// <summary>Opt-in, read-only hardware UI verification. Captures this app's visual tree, never the desktop.</summary>
internal static partial class UiVerification
{
    private static readonly string[] args = Environment.GetCommandLineArgs();
    public static bool IsInputEditRequested => args.Contains("--input-verify");
    public static bool IsCatalogRequested => args.Contains("--catalog-verify");
    public static bool IsRequested => args.Contains("--ui-verify") || IsInputEditRequested || IsCatalogRequested;
    public static bool HardwareWritesEnabled { get; private set; }
    public static string OutputDirectory { get { var index = Array.IndexOf(args, IsCatalogRequested ? "--catalog-verify" : IsInputEditRequested ? "--input-verify" : "--ui-verify"); return Path.GetFullPath(index >= 0 && index + 1 < args.Length ? args[index + 1] : "ui-verification"); } }
    public static string SettingsDirectory => Path.Combine(OutputDirectory, "isolated-settings", Environment.ProcessId.ToString());
    public const int ViewportWidth = 3840, ViewportHeight = 2088;
    private static bool running;
    public static async Task RunAsync(MainPage page, Controls.WorkspaceHost tabs)
    {
        if (running) return; running = true;
        var at = Array.IndexOf(args, "--ui-verify");
        var folder = Path.GetFullPath(at + 1 < args.Length ? args[at + 1] : "ui-verification"); Directory.CreateDirectory(folder);
        var checks = new List<object>(); var vm = page.ViewModel;
        GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundStart);
        var buildHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(typeof(UiVerification).Assembly.Location)));
        var originalMacro = vm.SelectedMacro;
        var selected = vm.SelectedProfile;
        try
        {
            if (!vm.HasSnapshot) await vm.ReadCommand.ExecuteAsync(null);
            Check("real-device-read", vm.HasSnapshot && !vm.HasError, vm.DeviceSummary);
            Check("format10-performance-enabled", vm.CanEditPerformance && vm.DpiStages.Count == 5, vm.PerformanceSummary);
            Check("dpi-read-values-preserved", string.Join(',', vm.DpiStages.Select(s => s.Value)) == "800,1200,1600,2400,3200", string.Join(',', vm.DpiStages.Select(s => s.Value)));
            Check("format10-lighting-enabled", vm.CanEditLighting, vm.CapabilityNote);
            // Read-only UI scenes use an isolated software draft; the physical mode is untouched here.
            vm.SelectedConfiguration = vm.Configurations.First(c => !c.Onboard);
            await Capture("01-assignments");
            vm.IsSideView = true; vm.ShowGShift = true; await Capture("02-gshift-side"); vm.IsSideView = false; vm.ShowGShift = false;
            var macroCount = vm.Macros.Count;
            page.BeginNewMacro(); await Capture("14-macro-name"); vm.SelectedMacro!.Name = "界面创建流程校验"; page.AdvanceNewMacroName(); await Capture("15-macro-type"); page.CancelNewMacro();
            Check("macro-create-cancel-restores-library", macroCount == vm.Macros.Count && ReferenceEquals(vm.SelectedMacro, originalMacro) && tabs.SelectedIndex == 0, "Name → type selection → cancel restored the library and assignment page without saving.");
            await VerifyMacroSaveAsync(page, tabs, Capture, Check);
            tabs.SelectedIndex = 1;
            var testMacro = new MacroEditor { Name = "序列校验", SelectedRepeat = MacroEditor.RepeatChoices[3], Script = "key Ctrl down\ntap A", HoldScript = "tap B\ndelay 80", ReleaseScript = "key Ctrl up\ntap C" };
            vm.Macros.Add(testMacro); vm.SelectedMacro = testMacro;
            vm.SelectedMacroSegment = vm.MacroSegments[1];
            testMacro.ShowUpDown = false; await Task.Delay(60); CheckCheckBox("显示按键按下/抬起", false);
            testMacro.ShowUpDown = true; await Task.Delay(60); CheckCheckBox("显示按键按下/抬起", true);
            var pressPhase = Descendants(page).OfType<Controls.MacroPhaseView>().Single(p => p.Segment == "press");
            var stepCaptions = Descendants(pressPhase).OfType<Button>().Select(b => b.Content as string).ToArray();
            Check("macro-show-down-up-expands-taps", stepCaptions.Contains("A ↓") && stepCaptions.Contains("A ↑") && testMacro.Script == "key Ctrl down\ntap A", "The display toggle expands a tap into down/up cards without modifying its execution script.");
            await vm.TestMacroCommand.ExecuteAsync(null);
            Check("sequence-executes-three-phases", vm.MacroExecutionTrace.Contains("Key 224 ↓") && vm.MacroExecutionTrace.Contains("Key 224 ↑") && vm.MacroExecutionTrace.Contains("Key 6 ↓"), vm.MacroExecutionTrace);
            vm.CompileMacroCommand.Execute(null);
            Check("unsupported-board-sequence-explained", vm.MacroPreview.Contains("板载限制") && vm.MacroPreview.Contains("软件执行"), vm.MacroPreview);
            await Capture("03-sequence-macro");
            vm.SelectedMacroSegment = vm.MacroSegments[2]; await Capture("04-sequence-release");
            tabs.SelectedIndex = 2;
            Check("live-performance-capability", vm.CanApplyPerformance, vm.PerformanceSummary);
            // Draft-only edits; no SetDpi, SetRate, mode change or flash command is issued.
            var first = vm.DpiStages[0]; first.LinkAxes = false; first.ValueY = "1200";
            await Capture("05-performance");
            vm.AdvancedDpi = true; await Capture("16-performance-xy"); vm.AdvancedDpi = false;
            Check("dpi-values-survive-template-initialization", string.Join(',', vm.DpiStages.Select(s => s.Value)) == "800,1200,1600,2400,3200", string.Join(',', vm.DpiStages.Select(s => s.Value)));
            vm.AllowExperimental = true;
            var draftPlan = vm.PrepareWrite();
            Check("format10-draft-plan", draftPlan.Changes.Any(c => c.Sector == vm.SelectedProfile!.Sector), draftPlan.Summary);
            vm.AllowExperimental = false;
            tabs.SelectedIndex = 3; vm.SelectedLightsyncMode = vm.LightsyncModes[0]; vm.SelectedLightsyncEffect = vm.LightsyncEffects.First(e => e.Id == "Cycle");
            await Capture("06-lightsync-preset");
            vm.SelectedLightsyncMode = vm.LightsyncModes[1]; vm.LightsyncColor = "#FF4F89"; vm.PaintSelectedZonesCommand.Execute(null); await Capture("07-lightsync-freestyle");
            Check("eight-led-paint", vm.LedZones.Count == 8 && vm.LedZones.All(z => z.Color == "#FF4F89"), "Eight independently addressable LED zones");
            vm.SelectedLightsyncMode = vm.LightsyncModes[2]; vm.LoadAnimationPresetCommand.Execute(null); await Capture("08-lightsync-animation");
            Check("animation-keyframes", vm.BuildLightsyncSettings().Frames.Length == 22, "Actual installed G HUB Ocean Wave: 22 eight-zone frames (full preset data)");
            await VerifyAnimationAsync(page, Capture, Check);
            vm.SelectedLightsyncMode = vm.LightsyncModes[0]; vm.SelectedLightsyncEffect = vm.LightsyncEffects.First(e => e.Id == "ScreenSampler"); await Capture("09-screen-sampler");
            Check("screen-regions", vm.BuildLightsyncSettings().Regions.Length == 8, "8 normalized monitor rectangles");
            var sampled = await Task.Run(() => new Services.ScreenSampler(0).Sample(vm.BuildLightsyncSettings().Regions));
            Check("native-screen-source", sampled.Length == 8, string.Join(',', sampled.Select(c => c.ToString())));
            vm.SelectedLightsyncEffect = vm.LightsyncEffects.First(e => e.Id == "AudioVisualizer"); await Capture("10-audio-visualizer");
            using (var meter = new Services.SystemAudioMeter())
            {
                // An all-zero stream exercises loopback while producing no audible test tone.
                using var silentOutput = new NAudio.Wave.WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, false, 50);
                silentOutput.Init(new NAudio.Wave.SilenceProvider(meter.Format)); silentOutput.Play();
                await Task.Delay(150);
                Check("native-audio-loopback", double.IsFinite(meter.Level), $"WASAPI default render endpoint opened; samples {meter.SamplesReceived}; RMS {meter.Level}");
                Check("native-audio-callback", meter.SamplesReceived > 0, "Silent output loopback delivered " + meter.SamplesReceived + " real PCM samples; no audible tone or volume change.");
            }
            tabs.SelectedIndex = 4; await Capture("11-device-settings");
            await VerifyHeroAsync(page, Capture, Check);
            if (args.Contains("--write-verify")) await VerifyImmediateWritesAsync(page, tabs, Capture, Check);
            tabs.SelectedIndex = 0;
            App.MainWindow.AppWindow.ResizeClient(new(1650, 1150));
            var compactRoot = (FrameworkElement)App.MainWindow.Content; compactRoot.Width = 1650 / App.MainWindow.DisplayScale; compactRoot.Height = 1150 / App.MainWindow.DisplayScale;
            await Capture("12-compact-window");
            tabs.SelectedIndex = 1; await NativeInputCheck();
            Check("no-software-input-engine", !vm.HostMacrosRunning && !vm.LightsyncRunning, args.Contains("--write-verify") ? "Opt-in board writes were separately backed up, read back, and restored; no software input engine ran." : "Only device reads and local draft edits were performed");
            vm.Macros.Remove(testMacro); vm.SelectedMacro = originalMacro;
            GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundEnd);
            await File.WriteAllTextAsync(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { FunctionalChecksPassed = true, VisualAcceptance = "See the separate requirements/review/verdict files; screenshot capture is not visual approval.", Capture = "WinUI RenderTargetBitmap", BuildSha256 = buildHash, ProcessId = Environment.ProcessId, ForegroundAtStart = foregroundStart, ForegroundAtEnd = foregroundEnd, Theme = ((FrameworkElement)App.MainWindow.Content).ActualTheme.ToString(), Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { Passed = false, Error = ex.ToString(), Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { await vm.ShutdownAsync(); App.MainWindow.Close(); }

        void Check(string name, bool passed, string evidence)
        {
            checks.Add(new { Name = name, Passed = passed, Evidence = evidence });
            if (!passed) throw new InvalidOperationException($"UI verification failed: {name}: {evidence}");
        }
        IEnumerable<DependencyObject> Descendants(DependencyObject element)
        {
            yield return element;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(element, i))) yield return child;
        }
        void CheckCheckBox(string caption, bool value)
        {
            var box = Descendants(page).OfType<CheckBox>().First(c => c.Content as string == caption);
            var mark = Descendants(box).OfType<Microsoft.UI.Xaml.Shapes.Path>().First(p => p.Name == "CheckMark");
            Check("checkbox-visual-" + value, box.IsChecked == value && mark.Visibility == (value ? Visibility.Visible : Visibility.Collapsed), $"{caption}: IsChecked={box.IsChecked}; mark={mark.Visibility}");
        }
        async Task NativeInputCheck()
        {
            if (!args.Contains("--native-input-verify"))
            {
                checks.Add(new { Name = "native-sendinput", Status = "Skipped", Reason = "Background verification: no activation, restore, mouse or keyboard input. Macro event semantics are checked with an isolated input sink." });
                return;
            }
            var keyEvents = new List<string>();
            var probe = new TextBox { Header = "本应用输入执行自检", PlaceholderText = "系统输入仅发送到此窗口", MinWidth = 340 };
            probe.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, e) => { keyEvents.Add("down:" + e.Key); }), true);
            probe.AddHandler(UIElement.KeyUpEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, e) => { keyEvents.Add("up:" + e.Key); }), true);
            var dialog = new ContentDialog { XamlRoot = page.XamlRoot, Title = "Windows 输入执行校验", Content = probe, CloseButtonText = "关闭" };
            var displayed = dialog.ShowAsync();
            try
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundProcess);
                if (foregroundProcess != Environment.ProcessId)
                {
                    checks.Add(new { Name = "native-sendinput", Status = "Skipped", Reason = "Windows foreground lock: no input was sent outside this app. Input execution is covered by the isolated sink and HID notification tests." });
                    return;
                }
                probe.Focus(FocusState.Programmatic);
                var sink = new GuardedSink();
                await using var runtime = new Core.Macros.MacroRuntime(sink);
                string fault = ""; runtime.Faulted += (_, ex) => fault = ex.Message;
                await runtime.Press("native-check", Core.Macros.MacroCompiler.CompileProgram("tap A\ndelay 50\ntap B", Core.Macros.MacroRepeat.Once));
                await Task.Delay(200);
                Check("native-sendinput", string.Join(',', keyEvents) == "down:A,up:A,down:B,up:B", string.Join(',', keyEvents) + (fault == "" ? "" : " Error: " + fault));
                probe.Text = string.Join(" → ", keyEvents); await Capture("13-native-input");
            }
            finally { dialog.Hide(); await displayed; }
        }
        async Task Capture(string name)
        {
            await Task.Delay(500); page.UpdateLayout();
            var root = (FrameworkElement)App.MainWindow.Content;
            var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(root);
            var buffer = await bitmap.GetPixelsAsync();
            if (bitmap.PixelWidth < 500 || bitmap.PixelHeight < 400 || buffer.Length == 0) throw new InvalidOperationException("UI 截图尺寸无效。");
            using var stream = new InMemoryRandomAccessStream(); var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, buffer.ToArray());
            await encoder.FlushAsync(); using var reader = new DataReader(stream.GetInputStreamAt(0)); await reader.LoadAsync((uint)stream.Size); var bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
            await File.WriteAllBytesAsync(Path.Combine(folder, name + ".png"), bytes);
            var layout = new List<object>();
            void Walk(DependencyObject element)
            {
                if (element is FrameworkElement { Visibility: Visibility.Collapsed }) return;
                if (element is FrameworkElement f && f.ActualWidth > 0 && f.ActualHeight > 0)
                {
                    string? text = f switch { TextBlock t => t.Text, TextBox t => t.Text, Button b => b.Content as string, Microsoft.UI.Xaml.Controls.Primitives.ToggleButton toggle => $"{toggle.Content as string ?? toggle.Name}: {toggle.IsChecked}", ComboBox c => c.SelectedItem?.ToString(), Microsoft.UI.Xaml.Controls.Primitives.RangeBase range => range.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => null };
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        try { var rect = f.TransformToVisual(root).TransformBounds(new(0, 0, f.ActualWidth, f.ActualHeight)); layout.Add(new { Type = f.GetType().Name, f.Name, Text = text, BoundsDip = new { rect.X, rect.Y, rect.Width, rect.Height } }); } catch (ArgumentException) { }
                    }
                }
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) Walk(VisualTreeHelper.GetChild(element, i));
            }
            Walk(root);
            GetWindowThreadProcessId(GetForegroundWindow(), out var foreground);
            await File.WriteAllTextAsync(Path.Combine(folder, name + ".layout.json"), JsonSerializer.Serialize(new { Capture = name, BuildSha256 = buildHash, ForegroundProcess = foreground, Scale = page.XamlRoot.RasterizationScale, State = new { tabs.SelectedIndex, vm.ShowGShift, vm.IsSideView, Macro = vm.SelectedMacro?.Name, MacroRepeat = vm.SelectedMacro?.SelectedRepeat.Label, Lightsync = vm.SelectedLightsyncMode.Label, Effect = vm.SelectedLightsyncEffect.Label, vm.BatteryText, vm.FirmwareText }, Nodes = layout }, new JsonSerializerOptions { WriteIndented = true }));
            checks.Add(new { Name = name, Passed = true, Width = bitmap.PixelWidth, Height = bitmap.PixelHeight, Page = new { page.ActualWidth, page.ActualHeight } });
        }
    }
    private sealed class GuardedSink : Core.Macros.IMacroInputSink
    {
        private readonly Core.Windows.WindowsInputSink sink = new();
        public void Send(Core.Macros.MacroInput input)
        {
            var foreground = GetForegroundWindow(); GetWindowThreadProcessId(foreground, out var process);
            if (process != Environment.ProcessId) throw new InvalidOperationException($"输入自检窗口不在前台，已取消输入（前台进程 {process}，自检进程 {Environment.ProcessId}）。");
            sink.Send(input);
        }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
