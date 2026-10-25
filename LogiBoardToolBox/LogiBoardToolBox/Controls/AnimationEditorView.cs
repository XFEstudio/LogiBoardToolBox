using System.ComponentModel;
using System.Globalization;
using LogiBoardToolBox.Core.Lighting;
using LogiBoardToolBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using AnimationDirection = LogiBoardToolBox.Core.Lighting.AnimationDirection;

namespace LogiBoardToolBox.Controls;

public sealed class AnimationEditorView : UserControl
{
    private MainPageViewModel Vm => App.MouseWorkspace;
    private readonly LightsyncMapView modelMap = new();
    private readonly StackPanel frames = new() { Orientation = Orientation.Horizontal, Spacing = 0 };
    private readonly Canvas timeline = new() { Height = 76, Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Line playhead = new() { Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 2, Y1 = 0, Y2 = 50 };
    private readonly Button play = Tool("▶", "animation-play");
    private bool seeking;
    private bool syncingPicker;
    private ComboBox? directionPicker;
    private PrecisionSlider? speedSlider;
    private ColorPicker? colorPicker;
    private readonly Dictionary<TransitionCurve, Button> curveButtons = [];
    public event EventHandler? Closed;
    public AnimationEditorView()
    {
        var root = new Grid { Background = Brush(Colors.Black), Padding = new(0, 84, 0, 24), RowSpacing = 18 };
        root.RowDefinitions.Add(new() { Height = new(62) }); root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = new(168) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var title = new TextBox { Width = 600, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 20, TextAlignment = TextAlignment.Center, Background = Brush(Colors.Transparent), BorderThickness = new(0), MaxLength = 50 };
        title.SetBinding(TextBox.TextProperty, new Microsoft.UI.Xaml.Data.Binding { Source = Vm, Path = new(nameof(Vm.AnimationName)), Mode = Microsoft.UI.Xaml.Data.BindingMode.TwoWay, UpdateSourceTrigger = Microsoft.UI.Xaml.Data.UpdateSourceTrigger.PropertyChanged });
        root.Children.Add(title);
        var stage = new Grid { ColumnSpacing = 40, Margin = new(140, 0, 48, 0) };
        stage.ColumnDefinitions.Add(new() { Width = new(320) }); stage.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        Grid.SetRow(stage, 1); root.Children.Add(stage);
        var options = new StackPanel { Spacing = 20, VerticalAlignment = VerticalAlignment.Center };
        options.Children.Add(Label("颜色"));
        var picker = new ColorPicker { IsAlphaEnabled = false, IsMoreButtonVisible = false, IsColorChannelTextInputVisible = false, IsColorSliderVisible = false, IsHexInputVisible = false, MaxHeight = 180, HorizontalAlignment = HorizontalAlignment.Left };
        colorPicker = picker;
        picker.ColorChanged += (_, e) => { if (syncingPicker || !Vm.AnimationEditing) return; Vm.LightsyncColor = $"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}"; Vm.PaintSelectedZonesCommand.Execute(null); RefreshFrames(); };
        options.Children.Add(picker);
        options.Children.Add(Label("过渡效果"));
        var curves = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        for (var i = 0; i < 5; i++) curves.ColumnDefinitions.Add(new() { Width = new(48) });
        curves.RowDefinitions.Add(new() { Height = new(36) }); curves.RowDefinitions.Add(new() { Height = new(36) });
        for (var i = 0; i < Vm.AnimationCurves.Count; i++)
        {
            var curve = Vm.AnimationCurves[i]; var button = Tool("", "animation-curve-" + curve); button.Width = 48; button.Height = 36; button.Padding = new(4); button.BorderBrush = Brush(Color(80)); button.BorderThickness = new(1);
            button.Content = CurveShape(curve, 36, 24, Colors.White); ToolTipService.SetToolTip(button, curve.ToString());
            curveButtons[curve] = button;
            button.Click += (_, _) => { if (Vm.SelectedAnimationFrame is { } frame) { frame.Curve = curve; frame.Smooth = curve != TransitionCurve.None; RefreshFrames(); } };
            button.CanDrag = true; button.DragStarting += (_, e) => e.Data.SetText("lighting-curve:" + curve);
            Grid.SetColumn(button, i % 5); Grid.SetRow(button, i / 5); curves.Children.Add(button);
        }
        options.Children.Add(curves); options.Children.Add(Label("默认循环方式"));
        var direction = new ComboBox { Width = 270, ItemsSource = new[] { "循环", "反向循环", "弹跳", "随机" }, SelectedIndex = (int)Vm.SelectedAnimationDirection, Background = Brush(Colors.Black), BorderThickness = new(0), FontSize = 13 };
        directionPicker = direction;
        direction.SelectionChanged += (_, _) => { if (direction.SelectedIndex >= 0) Vm.SelectedAnimationDirection = (AnimationDirection)direction.SelectedIndex; };
        options.Children.Add(direction); options.Children.Add(Label("默认速度"));
        var speed = new PrecisionSlider { Minimum = 50, Maximum = 1000, StepFrequency = 10, Value = Vm.AnimationDefaultSpeed, Width = 270 };
        speedSlider = speed;
        speed.ValueChanged += (_, e) => Vm.AnimationDefaultSpeed = e.NewValue; options.Children.Add(speed);
        stage.Children.Add(new ScrollViewer { Content = options, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var model = modelMap; model.Zones = Vm.LedZones; model.ShowSelectors = true; model.DeviceModel = Vm.DeviceModel;
        model.ZoneInvoked += (_, zone) => { foreach (var z in Vm.LedZones) z.Selected = ReferenceEquals(z, zone); SyncPicker(); };
        Grid.SetColumn(model, 1); stage.Children.Add(model);
        var timelineBox = new StackPanel { Spacing = 14, Margin = new(500, 0, 72, 0), VerticalAlignment = VerticalAlignment.Center };
        timelineBox.Children.Add(new TextBlock { Text = "选择要编辑的帧。", FontSize = 12, Foreground = Brush(Color(150)), HorizontalAlignment = HorizontalAlignment.Center });
        var timelineRow = new Grid { ColumnSpacing = 14 };
        timelineRow.ColumnDefinitions.Add(new() { Width = new(42) }); timelineRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); timelineRow.ColumnDefinitions.Add(new() { Width = new(42) });
        play.Click += (_, _) => TogglePlayback(); timelineRow.Children.Add(play);
        timeline.Children.Add(frames); timeline.Children.Add(playhead);
        var scroll = new ScrollViewer { Content = timeline, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled };
        Grid.SetColumn(scroll, 1); timelineRow.Children.Add(scroll);
        var add = Tool("", "animation-add-frame"); add.Height = 36; add.Padding = new(0); add.Content = new FontIcon { Glyph = "\uE710", FontSize = 18 }; add.Click += (_, _) => { Vm.InsertAnimationFrame(); RefreshFrames(); }; Grid.SetColumn(add, 2); timelineRow.Children.Add(add);
        timelineBox.Children.Add(timelineRow);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var zoom in new[] { 25, 50, 100 }) { var button = Tool(zoom + "%", "animation-zoom-" + zoom); button.Width = 50; button.FontSize = 11; button.Click += (_, _) => { Vm.AnimationZoom = zoom; RefreshFrames(); }; tools.Children.Add(button); }
        var duplicate = Tool("复制帧", "animation-duplicate-frame"); duplicate.Width = 75; duplicate.Click += (_, _) => { Vm.DuplicateAnimationFrame(); RefreshFrames(); }; tools.Children.Add(duplicate);
        var remove = Tool("删除帧", "animation-remove-frame"); remove.Width = 75; remove.Click += (_, _) => { Vm.RemoveAnimationFrameCommand.Execute(null); Vm.ReflowAnimation(); RefreshFrames(); }; tools.Children.Add(remove);
        timelineBox.Children.Add(tools); Grid.SetRow(timelineBox, 2); root.Children.Add(timelineBox);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center };
        var cancel = Tool("取消", "animation-cancel"); cancel.Width = 160; cancel.Background = Brush(Color(30)); cancel.Click += (_, _) => Cancel();
        var save = Tool("保存", "animation-save"); save.Width = 160; save.Background = Brush(Windows.UI.Color.FromArgb(255, 0, 169, 224)); save.Foreground = Brush(Colors.Black); save.Click += async (_, _) => await SaveAsync();
        footer.Children.Add(cancel); footer.Children.Add(save); Grid.SetRow(footer, 3); root.Children.Add(footer);
        var back = Tool("←", "animation-back"); back.HorizontalAlignment = HorizontalAlignment.Left; back.VerticalAlignment = VerticalAlignment.Top; back.Margin = new(12, -62, 0, 0); back.Click += (_, _) => Cancel(); root.Children.Add(back);
        Content = root;
        Loaded += (_, _) => { Vm.AnimationTimelineChanged += ModelChanged; Vm.PropertyChanged += WorkspaceChanged; modelMap.DeviceModel = Vm.DeviceModel; RefreshFrames(); };
        Unloaded += (_, _) => { Vm.AnimationTimelineChanged -= ModelChanged; Vm.PropertyChanged -= WorkspaceChanged; };
        timeline.PointerPressed += (_, e) => { if (e.GetCurrentPoint(timeline).Position.Y < 12) { seeking = true; timeline.CapturePointer(e.Pointer); Scrub(e.GetCurrentPoint(timeline).Position.X); e.Handled = true; } };
        timeline.PointerMoved += (_, e) => { if (seeking) Scrub(e.GetCurrentPoint(timeline).Position.X); };
        timeline.PointerReleased += (_, e) => { seeking = false; timeline.ReleasePointerCapture(e.Pointer); };
    }
    private void Scrub(double x) { Vm.SeekAnimation(x / PixelsPerMillisecond); RefreshPlayhead(); }
    private double PixelsPerMillisecond => Math.Max(48 * Vm.AnimationZoom / 100 / Vm.AnimationDefaultSpeed,
        6d / Vm.AnimationFrames.Select(f => double.TryParse(f.Duration, out var duration) && duration > 0 ? duration : 100).DefaultIfEmpty(100).Min());
    private void ModelChanged(object? sender, EventArgs e) => RefreshFrames();
    private void WorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Vm.DeviceModel)) modelMap.DeviceModel = Vm.DeviceModel;
        if (e.PropertyName is nameof(Vm.AnimationPlayhead) or nameof(Vm.PlayLightsyncPreview)) RefreshPlayhead();
        if (e.PropertyName == nameof(Vm.SelectedAnimationDirection) && directionPicker is not null) directionPicker.SelectedIndex = (int)Vm.SelectedAnimationDirection;
        if (e.PropertyName == nameof(Vm.AnimationDefaultSpeed) && speedSlider is not null) speedSlider.Value = Vm.AnimationDefaultSpeed;
        if (e.PropertyName == nameof(Vm.SelectedAnimationFrame)) SyncPicker();
    }
    private void SyncPicker()
    {
        if (colorPicker is null) return;
        var zone = Vm.LedZones.FirstOrDefault(z => z.Selected) ?? Vm.LedZones[0]; var c = Rgb.Parse(zone.Color);
        syncingPicker = true; try { colorPicker.Color = Windows.UI.Color.FromArgb(255, c.R, c.G, c.B); } finally { syncingPicker = false; }
    }
    public void TogglePlayback() { Vm.PlayLightsyncPreview = !Vm.PlayLightsyncPreview; RefreshPlayhead(); }
    public void Cancel() { Vm.CancelAnimationEdit(); Closed?.Invoke(this, EventArgs.Empty); }
    public async Task SaveAsync() { try { await Vm.SaveAnimationEditAsync(); Closed?.Invoke(this, EventArgs.Empty); } catch (Exception error) { Vm.ShowError(error); } }
    public void RefreshFrames()
    {
        if (directionPicker is not null) directionPicker.SelectedIndex = (int)Vm.SelectedAnimationDirection;
        if (speedSlider is not null) speedSlider.Value = Vm.AnimationDefaultSpeed;
        foreach (var (curve, button) in curveButtons) button.BorderBrush = Brush(curve == Vm.SelectedAnimationFrame?.Curve ? Windows.UI.Color.FromArgb(255, 0, 169, 224) : Color(80));
        SyncPicker();
        frames.Children.Clear(); var i = 0;
        foreach (var frame in Vm.AnimationFrames)
        {
            var index = i++; var duration = int.TryParse(frame.Duration, out var value) ? value : 100;
            var width = duration * PixelsPerMillisecond;
            var panel = new StackPanel { Spacing = 5, Width = width };
            var button = Tool("", "animation-frame-" + index); button.Width = width; button.Height = 36; button.Padding = new(0); button.BorderThickness = new(ReferenceEquals(Vm.SelectedAnimationFrame, frame) ? 2 : 1); button.BorderBrush = Brush(ReferenceEquals(Vm.SelectedAnimationFrame, frame) ? Colors.White : Color(75));
            var fill = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
            for (var led = 0; led < frame.Colors.Length; led++) { var c = frame.Colors[led]; fill.GradientStops.Add(new() { Color = Windows.UI.Color.FromArgb(255, c.R, c.G, c.B), Offset = frame.Colors.Length == 1 ? 0 : led / (double)(frame.Colors.Length - 1) }); }
            var drawing = new Grid { Background = fill }; drawing.Children.Add(CurveShape(frame.Curve, Math.Max(20, width - 4), 28, Colors.Black)); button.Content = drawing;
            button.Click += (_, _) => Vm.SelectAnimationFrame(frame);
            button.DoubleTapped += (_, _) => EditDuration(button, frame);
            button.CanDrag = true; button.DragStarting += (_, e) => { Vm.PlayLightsyncPreview = false; e.Data.SetText("lighting-frame:" + index); e.Data.RequestedOperation = DataPackageOperation.Move; };
            button.AllowDrop = true; button.DragOver += (_, e) => { e.AcceptedOperation = DataPackageOperation.Move; };
            button.Drop += async (_, e) =>
            {
                try
                {
                    var data = await e.DataView.GetTextAsync();
                    if (data.StartsWith("lighting-frame:", StringComparison.Ordinal) && int.TryParse(data[15..], out var from)) Vm.MoveAnimationFrame(from, index);
                    else if (data.StartsWith("lighting-curve:", StringComparison.Ordinal) && Enum.TryParse<TransitionCurve>(data[15..], out var curve)) { frame.Curve = curve; frame.Smooth = curve != TransitionCurve.None; }
                }
                catch (Exception error) { Vm.ShowError(error); }
            };
            var menu = new MenuFlyout(); var duplicate = new MenuFlyoutItem { Text = "复制帧" }; duplicate.Click += (_, _) => { Vm.SelectAnimationFrame(frame); Vm.DuplicateAnimationFrame(); }; menu.Items.Add(duplicate);
            var remove = new MenuFlyoutItem { Text = "删除帧" }; remove.Click += (_, _) => { Vm.SelectAnimationFrame(frame); Vm.RemoveAnimationFrameCommand.Execute(null); Vm.ReflowAnimation(); RefreshFrames(); }; menu.Items.Add(remove);
            var timing = new MenuFlyoutItem { Text = "设置帧时长" }; timing.Click += (_, _) => EditDuration(button, frame); menu.Items.Add(timing); button.ContextFlyout = menu;
            var timeLabel = duration.ToString(CultureInfo.InvariantCulture) + " ms";
            ToolTipService.SetToolTip(button, frame.Name + " · " + timeLabel);
            AutomationProperties.SetName(button, frame.Name + " · " + timeLabel);
            panel.Children.Add(button); panel.Children.Add(new TextBlock { Text = width >= 44 ? timeLabel : "", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brush(Color(160)) }); frames.Children.Add(panel);
        }
        Canvas.SetTop(frames, 12); timeline.Width = Math.Max(400, Vm.LightsyncPeriod * PixelsPerMillisecond + 60); RefreshPlayhead();
    }
    private void EditDuration(Button anchor, LightingFrameEditor frame)
    {
        var field = new NumberBox { Header = "帧时长（毫秒）", Minimum = 1, Maximum = 60000, Value = int.Parse(frame.Duration), Width = 190, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var save = new Button { Content = "应用" }; var flyout = new Flyout { Content = new StackPanel { Spacing = 12, Children = { field, save } } };
        save.Click += (_, _) => { if (double.IsFinite(field.Value)) frame.Duration = ((int)field.Value).ToString(); flyout.Hide(); RefreshFrames(); }; flyout.ShowAt(anchor);
    }
    private void RefreshPlayhead() { play.Content = Vm.PlayLightsyncPreview ? "■" : "▶"; playhead.X1 = playhead.X2 = Vm.AnimationPlayhead * PixelsPerMillisecond; }
    private static Polyline CurveShape(TransitionCurve curve, double width, double height, Windows.UI.Color color)
    {
        var shape = new Polyline { Width = width, Height = height, Stroke = Brush(color), StrokeThickness = 1.25, VerticalAlignment = VerticalAlignment.Center };
        for (var step = 0; step <= 40; step++) shape.Points.Add(new(width * step / 40, height * (1 - LightingTransition.Evaluate(curve, step / 40d)))); return shape;
    }
    private static Windows.UI.Color Color(byte gray) => Windows.UI.Color.FromArgb(255, gray, gray, gray);
    private static SolidColorBrush Brush(Windows.UI.Color color) => new(color);
    private static TextBlock Label(string text) => new() { Text = text, FontSize = 13, Foreground = Brush(Color(170)) };
    private static Button Tool(string text, string id) { var button = new Button { Content = text, Height = 28, MinHeight = 0, MinWidth = 0, Width = 42, Padding = new(4), FontSize = 12, Background = Brush(Colors.Transparent), BorderThickness = new(0), CornerRadius = new(0) }; AutomationProperties.SetAutomationId(button, id); return button; }
}
