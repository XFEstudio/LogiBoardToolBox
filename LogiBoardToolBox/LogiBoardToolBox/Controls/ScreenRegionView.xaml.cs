using System.ComponentModel;
using System.Globalization;
using LogiBoardToolBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace LogiBoardToolBox.Controls;

public sealed partial class ScreenRegionView : UserControl
{
    public static readonly DependencyProperty RegionsProperty = DependencyProperty.Register(nameof(Regions), typeof(object), typeof(ScreenRegionView), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty SelectedRegionProperty = DependencyProperty.Register(nameof(SelectedRegion), typeof(object), typeof(ScreenRegionView), new PropertyMetadata(null, Changed));
    public object Regions { get => GetValue(RegionsProperty); set => SetValue(RegionsProperty, value); }
    public object SelectedRegion { get => GetValue(SelectedRegionProperty); set => SetValue(SelectedRegionProperty, value); }
    public event EventHandler<SampleRegionEditor>? RegionSelected;
    private readonly List<(Border Box, SampleRegionEditor Region)> boxes = [];
    private SampleRegionEditor? dragging;
    private Windows.Foundation.Point origin;
    private Core.Lighting.SampleRegion? initial;
    private bool resizing;
    public ScreenRegionView() { InitializeComponent(); Loaded += (_, _) => Build(); Unloaded += (_, _) => Detach(); }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) { var view = (ScreenRegionView)d; if (e.Property == RegionsProperty) view.Build(); else view.Refresh(); }
    private void Detach() { foreach (var (_, region) in boxes) region.PropertyChanged -= RegionChanged; }
    private void Build()
    {
        if (Surface is null) return; Detach(); foreach (var (box, _) in boxes) Surface.Children.Remove(box); boxes.Clear();
        if (Regions is not IEnumerable<SampleRegionEditor> regions) return;
        foreach (var region in regions)
        {
            var box = new Border { BorderThickness = new(2), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 0, 174, 230)), Child = new Grid { Children = { new TextBlock { Text = region.Label, Margin = new(5), Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) }, new TextBlock { Text = "◢", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) } } } };
            box.PointerPressed += (sender, e) =>
            {
                var p = e.GetCurrentPoint(Surface); if (!p.Properties.IsLeftButtonPressed) return;
                try { initial = region.Build(); initial.Validate(); } catch (Exception) { return; }
                RegionSelected?.Invoke(this, region); dragging = region; origin = p.Position;
                var local = e.GetCurrentPoint(box).Position; resizing = local.X > box.ActualWidth - 18 && local.Y > box.ActualHeight - 18;
                box.CapturePointer(e.Pointer); e.Handled = true;
            };
            box.PointerMoved += Drag;
            box.PointerReleased += (s, e) => { dragging = null; box.ReleasePointerCapture(e.Pointer); };
            box.PointerCaptureLost += (_, _) => dragging = null;
            boxes.Add((box, region)); Surface.Children.Add(box); region.PropertyChanged += RegionChanged;
        }
        Refresh();
    }
    private void Drag(object sender, PointerRoutedEventArgs e)
    {
        if (dragging is null || initial is null) return;
        var p = e.GetCurrentPoint(Surface).Position; var dx = (p.X - origin.X) / 640; var dy = (p.Y - origin.Y) / 360;
        if (resizing)
        { dragging.Width = Percent(Math.Clamp(initial.Width + dx, .03, 1 - initial.X)); dragging.Height = Percent(Math.Clamp(initial.Height + dy, .03, 1 - initial.Y)); }
        else { dragging.X = Percent(Math.Clamp(initial.X + dx, 0, 1 - initial.Width)); dragging.Y = Percent(Math.Clamp(initial.Y + dy, 0, 1 - initial.Height)); }
        e.Handled = true;
    }
    private static string Percent(double value) => (value * 100).ToString("0.###", CultureInfo.InvariantCulture);
    private void RegionChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
    private void Refresh()
    {
        foreach (var (box, region) in boxes)
        {
            try
            {
                var r = region.Build(); r.Validate(); Canvas.SetLeft(box, r.X * 640); Canvas.SetTop(box, r.Y * 360); box.Width = r.Width * 640; box.Height = r.Height * 360;
                box.BorderBrush = new SolidColorBrush(ReferenceEquals(region, SelectedRegion) ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(255, 0, 150, 200));
                Canvas.SetZIndex(box, ReferenceEquals(region, SelectedRegion) ? 2 : 1);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException) { }
        }
    }
}
