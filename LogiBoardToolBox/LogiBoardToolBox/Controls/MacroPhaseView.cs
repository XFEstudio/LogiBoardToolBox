using LogiBoardToolBox.ViewModels;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace LogiBoardToolBox.Controls;

/// <summary>A G HUB style phase: horizontal editable actions followed by an insert control.</summary>
public sealed partial class MacroPhaseView : UserControl
{
    public static readonly DependencyProperty ScriptProperty = DependencyProperty.Register(nameof(Script), typeof(string), typeof(MacroPhaseView), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty SegmentProperty = DependencyProperty.Register(nameof(Segment), typeof(string), typeof(MacroPhaseView), new PropertyMetadata("press", Changed));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(MacroPhaseView), new PropertyMetadata("点击", Changed));
    public static readonly DependencyProperty ShowUpDownProperty = DependencyProperty.Register(nameof(ShowUpDown), typeof(bool), typeof(MacroPhaseView), new PropertyMetadata(true, Changed));
    public bool ShowUpDown { get => (bool)GetValue(ShowUpDownProperty); set => SetValue(ShowUpDownProperty, value); }
    public string Script { get => (string)GetValue(ScriptProperty); set => SetValue(ScriptProperty, value); }
    public string Segment { get => (string)GetValue(SegmentProperty); set => SetValue(SegmentProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public event EventHandler? RecordRequested;
    private MainPageViewModel Model => App.MouseWorkspace;
    private readonly StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 12, Padding = new(24, 20, 12, 6) };
    private readonly TextBlock title = new() { FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
    internal const int StepsPerPage = 96;
    private int page;
    private readonly TextBlock pageText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly Button previous = new() { Content = "上一段", Padding = new(8, 3, 8, 3) };
    private readonly Button next = new() { Content = "下一段", Padding = new(8, 3, 8, 3) };
    private readonly StackPanel pager = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
    internal int RenderedActionCount => actions.Children.OfType<Button>().Count() - 1;
    internal int CurrentPage => page;
    internal void NextPage() { if (next.IsEnabled) { page++; Rebuild(); } }
    public MacroPhaseView()
    {
        var panel = new StackPanel { Spacing = 6, MinHeight = 116 };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
        pager.Children.Add(previous); pager.Children.Add(pageText); pager.Children.Add(next);
        heading.Children.Add(title); heading.Children.Add(pager);
        previous.Click += (_, _) => { if (page > 0) { page--; Rebuild(); } };
        next.Click += (_, _) => NextPage();
        panel.Children.Add(heading); panel.Children.Add(new ScrollViewer { Content = actions, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled }); Content = panel;
        Loaded += (_, _) => Rebuild();
    }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((MacroPhaseView)d).Rebuild();
    private void Select(int? step = null)
    {
        Model.SelectedMacroSegment = Model.MacroSegments.Single(s => s.Id == Segment);
        Model.SelectedStep = step is int at ? Model.Timeline.ElementAtOrDefault(at) : Model.Timeline.LastOrDefault();
    }
    private void Rebuild()
    {
        title.Text = Title; actions.Children.Clear();
        var lines = (Script ?? "").Replace("\r", "").Split('\n').Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#')).ToArray();
        var pages = Math.Max(1, (lines.Length + StepsPerPage - 1) / StepsPerPage);
        page = Math.Clamp(page, 0, pages - 1);
        pager.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        previous.IsEnabled = page > 0; next.IsEnabled = page + 1 < pages;
        pageText.Text = $"第 {page + 1} / {pages} 段 · 共 {lines.Length:N0} 步";
        for (var i = page * StepsPerPage; i < Math.Min(lines.Length, (page + 1) * StepsPerPage); i++)
        {
            var index = i; MacroStepEditor step;
            try { step = MacroStepEditor.FromScript(lines[i]); } catch (NotSupportedException) { continue; }
            if (!ShowUpDown && step.Kind.Id is "key-up" or "mouse-up" or "consumer-up") continue;
            var label = step.Kind.Id switch { "delay" => step.Value + " ms", "key-down" or "mouse-down" or "consumer-down" => step.Value + (ShowUpDown ? " ↓" : ""), "key-up" or "mouse-up" or "consumer-up" => step.Value + " ↑", "wait-release" => "松开", _ => step.Value };
            var labels = ShowUpDown && step.Kind.Id == "tap" ? new[] { step.Value + " ↓", step.Value + " ↑" } : new[] { label };
            foreach (var displayed in labels)
            {
                var button = CreateStepButton(step, displayed);
                button.Click += (_, _) => { Select(index); ShowStepEditor(button, index); };
                button.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Delete) { Select(index); Model.RemoveStepCommand.Execute(null); e.Handled = true; } };
                button.DragStarting += (_, e) => { e.Data.SetText($"LogiBoard.Step:{Segment}:{index}"); e.AllowedOperations = DataPackageOperation.Move; };
                button.DragOver += (_, e) => e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.Text) ? DataPackageOperation.Move : DataPackageOperation.None;
                button.Drop += async (_, e) => { if (e.DataView.Contains(StandardDataFormats.Text)) MoveStep(await e.DataView.GetTextAsync(), index); };
                var menu = new MenuFlyout(); var delete = new MenuFlyoutItem { Text = "删除" }; delete.Click += (_, _) => { Select(index); Model.RemoveStepCommand.Execute(null); }; menu.Items.Add(delete); button.ContextFlyout = menu;
                actions.Children.Add(button);
            }
        }
        var insert = new Button { Content = "+", FontSize = 30, Width = 40, Height = 40, MinHeight = 0, Padding = new(0), VerticalAlignment = VerticalAlignment.Center, Background = Brush(57), BorderThickness = new(0), CornerRadius = new(2) };
        var flyout = new MenuFlyout();
        void Item(string label, string? kind)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => { Select(); if (kind is null) RecordRequested?.Invoke(this, EventArgs.Empty); else Model.AddStepCommand.Execute(kind); };
            flyout.Items.Add(item);
        }
        Item("● 记录按键", null); Item("击键", "tap"); Item("按键按下", "key-down"); Item("按键抬起", "key-up"); Item("延迟", "delay"); Item("鼠标按下", "mouse-down"); Item("鼠标松开", "mouse-up"); Item("滚轮", "wheel"); Item("水平滚轮", "pan"); Item("鼠标移动", "move"); Item("媒体按下", "consumer-down"); Item("媒体抬起", "consumer-up");
        insert.Flyout = flyout; actions.Children.Add(insert);
        if (lines.Length == 0) actions.Children.Add(new TextBlock { Text = "立即开始", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 0, 8) });
    }

    private Button CreateStepButton(MacroStepEditor step, string label)
    {
        object content = label;
        var name = label;
        if (step.Kind.Id == "delay")
        {
            content = MacroStepVisual.Delay(step.Value);
            name = $"延迟 {step.Value} 毫秒";
        }
        else if (step.Kind.Id is "mouse-down" or "mouse-up")
        {
            var down = step.Kind.Id == "mouse-down";
            try
            {
                var number = Core.Macros.MacroCompiler.Number(step.Value, 1, 16);
                var mouse = number switch { 1 => "左键", 2 => "右键", 3 => "中键", 4 => "后退侧键", 5 => "前进侧键", _ => $"鼠标按键 {number}" };
                name = mouse + (down ? "按下" : "抬起");
                content = number <= 3 ? MacroStepVisual.Mouse(number, down, ShowUpDown) : mouse + (ShowUpDown ? down ? " ↓" : " ↑" : "");
            }
            catch (Exception error) when (error is FormatException or OverflowException) { name = "鼠标按键 " + label; }
        }
        var visual = content is FrameworkElement;
        var button = new Button
        {
            Content = content, MinWidth = 42, Height = visual ? 64 : 40,
            Padding = visual ? new(4, 0, 4, 0) : new(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center, FontSize = 13,
            Background = visual ? Brush(0) : Brush(57), BorderBrush = Brush(85),
            BorderThickness = new(visual ? 0 : 1), CornerRadius = new(2), CanDrag = true, AllowDrop = true
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        return button;
    }
    private void ShowStepEditor(Button anchor, int index)
    {
        Select(index); if (Model.SelectedStep is not { } step) return;
        var kind = new ComboBox { ItemsSource = step.Kinds, DisplayMemberPath = "Label", SelectedItem = step.Kind, HorizontalAlignment = HorizontalAlignment.Stretch };
        var value = new TextBox { Text = step.Value, PlaceholderText = "按键、数值或 X Y", MinWidth = 190 };
        var apply = new Button { Content = "完成", HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(kind); panel.Children.Add(value); panel.Children.Add(apply);
        var popup = new Flyout { Content = panel };
        apply.Click += (_, _) => { Select(index); if (Model.SelectedStep is { } target) { target.Kind = (EditorChoice)kind.SelectedItem; target.Value = value.Text; } popup.Hide(); };
        popup.ShowAt(anchor);
    }
    private void MoveStep(string text, int target)
    {
        var parts = text.Split(':'); if (parts.Length != 3 || parts[0] != "LogiBoard.Step" || !int.TryParse(parts[2], out var from) || Model.SelectedMacro is not { } macro) return;
        string Read(string segment) => segment switch { "hold" => macro.HoldScript, "release" => macro.ReleaseScript, _ => macro.Script };
        void Write(string segment, string value) { switch (segment) { case "hold": macro.HoldScript = value; break; case "release": macro.ReleaseScript = value; break; default: macro.Script = value; break; } }
        var source = Read(parts[1]).Replace("\r", "").Split('\n').Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#')).ToList();
        if (from < 0 || from >= source.Count) return; var item = source[from]; source.RemoveAt(from);
        var destination = parts[1] == Segment ? source : Read(Segment).Replace("\r", "").Split('\n').Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#')).ToList();
        destination.Insert(Math.Min(target, destination.Count), item); Write(parts[1], string.Join('\n', source)); Write(Segment, string.Join('\n', destination)); Select();
    }
    private static SolidColorBrush Brush(int value) => new(Windows.UI.Color.FromArgb(255, (byte)value, (byte)value, (byte)value));
}
