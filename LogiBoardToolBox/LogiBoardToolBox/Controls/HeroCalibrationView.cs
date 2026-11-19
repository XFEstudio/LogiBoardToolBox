using LogiBoardToolBox.Core.Windows;
using Microsoft.UI;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace LogiBoardToolBox.Controls;

/// <summary>The source/DPI/movement/result flow uses physical Raw Input and a sensor completion event.</summary>
public sealed partial class HeroCalibrationView : UserControl
{
    private MainPageViewModel Vm => App.MouseWorkspace;
    private readonly StackPanel body = new() { Spacing = 24, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock title = new() { FontSize = 32, TextAlignment = TextAlignment.Center };
    private readonly TextBlock description = new() { FontSize = 15, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = new SolidColorBrush(Color(175)) };
    private readonly ComboBox source = new() { Header = "选择源鼠标", DisplayMemberPath = nameof(SourceMouse.Label), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox dpi = new() { Header = "源鼠标 DPI", Value = 800, Minimum = 50, Maximum = 100000, Width = 240, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 100, Width = 600 };
    private readonly TextBox name = new() { Header = "校准名称", Text = "自定义校准", Width = 360, MaxLength = 64, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button next = Action("开始", "hero-next");
    private readonly Button back = Action("取消", "hero-cancel");
    private CancellationTokenSource? operation;
    private int step;
    private short? measured;
    private sealed record SourceMouse(RawMouseDevice Device, string Label);
    public event EventHandler? Closed;
    public string State => step switch { 0 => "intro", 1 => "source", 2 => "movement", 3 => "result", _ => "error" };
    public HeroCalibrationView()
    {
        var root = new Grid { Background = new SolidColorBrush(Colors.Black) };
        body.Children.Add(title); body.Children.Add(description); body.Children.Add(source); body.Children.Add(dpi); body.Children.Add(progress); body.Children.Add(name);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(back); buttons.Children.Add(next); body.Children.Add(buttons); root.Children.Add(body); Content = root;
        next.Click += async (_, _) => await AdvanceAsync(); back.Click += async (_, _) => await CancelAsync();
    }
    public void Begin() { operation?.Cancel(); step = 0; measured = null; Show(); }
    private void Show()
    {
        source.Visibility = dpi.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        progress.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        name.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        next.IsEnabled = step != 2;
        title.Text = step switch { 0 => "HERO 传感器校准", 1 => "复制灵敏度", 2 => "移动鼠标", 3 => "校准详情", _ => "校准失败" };
        description.Text = step switch { 0 => "调整一款鼠标，使其手感与另一款鼠标一致。", 1 => "选择源鼠标并输入它当前的 DPI。将两只鼠标紧贴，保持传感器对齐。", 2 => "保持传感器对齐，同时水平移动两只鼠标约 10 英寸（25.4 厘米）。", 3 => $"传感器已返回校准完成事件。灵敏度修正：{measured / 1024d:P2}。保存此校准或取消以恢复。", _ => Vm.HeroCalibrationStatus };
        next.Content = step switch { 0 => "开始", 1 => "下一步", 2 => "正在分析…", 3 => "保存", _ => "重试" };
        progress.Value = 0;
    }
    public async Task AdvanceAsync()
    {
        try
        {
            if (step == 0) { source.ItemsSource = RawMouseMonitor.Devices().Select((d, i) => new SourceMouse(d, $"鼠标 {i + 1} · {d.Path.Split('#').ElementAtOrDefault(1)}")).ToArray(); source.SelectedIndex = -1; step = 1; Show(); return; }
            if (step == 4) { await Vm.RetryHeroAsync(); step = 1; Show(); return; }
            if (step == 1)
            {
                await Vm.RetryHeroAsync();
                if (Vm.IsEditingOnboard) throw new InvalidOperationException("HERO 传感器校准需要先选择软件配置。");
                if (Vm.AdvancedState?.CalibrationSupported != true) { step = 4; Show(); return; }
                if (source.SelectedItem is not SourceMouse chosen || !double.IsFinite(dpi.Value)) throw new InvalidOperationException("请选择另一只源鼠标并输入其当前 DPI。");
                operation = new(); step = 2; Show();
                measured = await Vm.MeasureHeroAsync(chosen.Device, (int)dpi.Value, new Progress<double>(v => progress.Value = v), operation.Token);
                step = 3; Show(); return;
            }
            if (step == 3 && measured is { } correction) { await Vm.SaveHeroCalibrationAsync(name.Text, correction); Closed?.Invoke(this, EventArgs.Empty); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Vm.HeroCalibrationStatus = error.Message; step = 4; Show(); }
    }
    public async Task CancelAsync()
    {
        operation?.Cancel();
        try { if (step is 2 or 3) await Vm.CancelHeroAsync(); }
        catch (Exception error) { Vm.ShowError(error); }
        finally { operation?.Dispose(); operation = null; Closed?.Invoke(this, EventArgs.Empty); }
    }
    private static Windows.UI.Color Color(byte gray) => Windows.UI.Color.FromArgb(255, gray, gray, gray);
    private static Button Action(string text, string id) { var b = new Button { Content = text, Width = 160, Height = 38, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 169, 224)), Foreground = new SolidColorBrush(Colors.Black), CornerRadius = new(0) }; AutomationProperties.SetAutomationId(b, id); return b; }
}
