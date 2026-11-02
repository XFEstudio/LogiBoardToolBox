using System.ComponentModel;
using LogiBoardToolBox.ViewModels;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Collections.Specialized;
using LogiBoardToolBox.Core.Devices;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LogiBoardToolBox.Controls;

public sealed partial class LightsyncMapView : UserControl
{
    public static readonly DependencyProperty ZonesProperty = DependencyProperty.Register(nameof(Zones), typeof(object), typeof(LightsyncMapView), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty ShowSelectorsProperty = DependencyProperty.Register(nameof(ShowSelectors), typeof(bool), typeof(LightsyncMapView), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty DeviceModelProperty = DependencyProperty.Register(nameof(DeviceModel), typeof(MouseModel), typeof(LightsyncMapView), new PropertyMetadata(null, Changed));
    public MouseModel? DeviceModel { get => (MouseModel?)GetValue(DeviceModelProperty); set => SetValue(DeviceModelProperty, value); }
    public object Zones { get => GetValue(ZonesProperty); set => SetValue(ZonesProperty, value); }
    public bool ShowSelectors { get => (bool)GetValue(ShowSelectorsProperty); set => SetValue(ShowSelectorsProperty, value); }
    private readonly List<LedZoneEditor> observed = [];
    private INotifyCollectionChanged? collection;
    internal string RenderedModelId { get; private set; } = "";
    public event EventHandler<LedZoneEditor>? ZoneInvoked;
    private readonly List<(Polygon[] Lights, Button Selector, LedZoneEditor Zone)> segments = [];
    public LightsyncMapView() { InitializeComponent(); Loaded += (_, _) => Rebuild(); Unloaded += (_, _) => { Detach(); if (collection is not null) collection.CollectionChanged -= CollectionChanged; collection = null; }; }
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((LightsyncMapView)d).Rebuild();
    private void Detach() { foreach (var z in observed) z.PropertyChanged -= ZoneChanged; observed.Clear(); }
    private void Rebuild()
    {
        if (LightSurface is null || OriginalModel is null || CatalogImage is null || Selectors is null) return;
        if (!ReferenceEquals(collection, Zones))
        { if (collection is not null) collection.CollectionChanged -= CollectionChanged; collection = Zones as INotifyCollectionChanged; if (collection is not null) collection.CollectionChanged += CollectionChanged; }
        Detach(); LightSurface.Children.Clear(); Selectors.Children.Clear(); segments.Clear();
        var model = DeviceModel ?? MouseCatalog.Unknown;
        RenderedModelId = model.Id;
        var original = model.Layout == "G502X";
        OriginalModel.Visibility = original ? Visibility.Visible : Visibility.Collapsed;
        var hasImage = model.IsKnown && !string.IsNullOrWhiteSpace(model.ImageAsset);
        CatalogImage.Visibility = !original && hasImage ? Visibility.Visible : Visibility.Collapsed;
        if (Zones is not IEnumerable<LedZoneEditor> source) return;
        if (!original)
        {
            if (hasImage)
            {
                var uri = new Uri("ms-appx:///" + model.ImageAsset.TrimStart('/'));
                if (CatalogImage.Source is not BitmapImage bitmap || bitmap.UriSource != uri) CatalogImage.Source = new BitmapImage(uri);
                CatalogImage.Width = 480 * model.ImageAspect;
            }
            foreach (var zone in source)
            {
                observed.Add(zone); zone.PropertyChanged += ZoneChanged;
                var y = 460 + zone.Index * 125;
                Selectors.Children.Add(new Line { X1 = 750, Y1 = y, X2 = 1030, Y2 = y, Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 85, 85, 85)), StrokeThickness = 1 });
                var selector = new Button { Content = zone.Label, Background = zone.PreviewBrush, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), BorderThickness = new(2), Width = 120, Height = 36, CornerRadius = new(18) };
                selector.Click += (_, _) => { if (ShowSelectors) { zone.Selected = !zone.Selected; ZoneInvoked?.Invoke(this, zone); } };
                Canvas.SetLeft(selector, 1030); Canvas.SetTop(selector, y - 18); Selectors.Children.Add(selector); segments.Add(([],selector,zone));
            }
            Refresh(); return;
        }
        // LED positions and radii use the installed device metadata coordinate system.
        (double X, double Y, double R)[] positions = [(430,1900,310),(820,2140,240),(1210,2000,300),(570,2110,240),(640,2150,240),(990,2180,240),(1120,2090,240),(1270,2210,280)];
        foreach (var zone in source.Take(8))
        {
            observed.Add(zone); zone.PropertyChanged += ZoneChanged;
            var p = positions[zone.Index]; var lights = new Polygon[16];
            // The model uses overlapping radial lights behind its transparent LED strip.
            // Nested clipped discs approximate that falloff without painting hard circles
            // outside the strip or adding a graphics runtime dependency.
            for (var ring = 0; ring < lights.Length; ring++)
            {
                var light = new Polygon { Points = ClipCircle(p.X, p.Y, p.R * (16 - ring) / 16), Fill = zone.PreviewBrush, Opacity = 1d / (16 - ring) };
                LightSurface.Children.Add(light); lights[ring] = light;
            }
            var selector = new Button { Width = 25, Height = 25, MinWidth = 0, MinHeight = 0, CornerRadius = new(13), Padding = new(0), Content = (zone.Index + 1).ToString(), FontSize = 11, Background = zone.PreviewBrush, BorderThickness = new(2), Visibility = ShowSelectors ? Visibility.Visible : Visibility.Collapsed };
            // Outside color-selector positions from metadata avoid covering adjacent zones.
            double[] startX = [.20,.53,.88,.31,.42,.64,.75,.90], startY = [.75,.83,.74,.80,.82,.83,.83,.85];
            double[] endX = [0,.45,1.17,.15,.30,.60,.75,1.17], endY = [1.08,1.08,.74,1.08,1.08,1.08,1.08,.85];
            var targetX = 607.5 + endX[zone.Index] * 285; var targetY = 280 + endY[zone.Index] * 480;
            var line = new Line { X1 = 607.5 + startX[zone.Index] * 285, Y1 = 280 + startY[zone.Index] * 480, X2 = targetX, Y2 = targetY, Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 85, 85, 85)), StrokeThickness = 1, Visibility = ShowSelectors ? Visibility.Visible : Visibility.Collapsed };
            Selectors.Children.Add(line); Canvas.SetLeft(selector, targetX - 12.5); Canvas.SetTop(selector, targetY - 12.5);
            selector.Click += (_, _) => { zone.Selected = !zone.Selected; ZoneInvoked?.Invoke(this, zone); };
            ToolTipService.SetToolTip(selector, zone.Label + " · 点击选择"); Selectors.Children.Add(selector); segments.Add((lights, selector, zone));
        }
        Refresh();
    }
    private void ZoneChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is nameof(LedZoneEditor.PreviewBrush) or nameof(LedZoneEditor.Selected)) Refresh(sender as LedZoneEditor); }
    private void Refresh(LedZoneEditor? changed = null) { foreach (var (lights, selector, zone) in segments) { if (changed is not null && !ReferenceEquals(changed, zone)) continue; foreach (var light in lights) light.Fill = zone.PreviewBrush; selector.Background = zone.PreviewBrush; selector.BorderBrush = new SolidColorBrush(zone.Selected ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Transparent); } }
    private static PointCollection ClipCircle(double x, double y, double radius)
    {
        // WinUI's UIElement.Clip accepts rectangles only. Clip the vector circles against the
        // LED mask, with its lower-right corner trimmed to this texture's silhouette.
        List<Windows.Foundation.Point> points = Enumerable.Range(0, 64).Select(i => new Windows.Foundation.Point(x + radius * Math.Cos(i * Math.PI / 32), y + radius * Math.Sin(i * Math.PI / 32))).ToList();
        Windows.Foundation.Point[] clip = [new(220,1900), new(1290,1600), new(1275,2300), new(1165,2420), new(430,2500)];
        for (var edge = 0; edge < clip.Length && points.Count > 0; edge++)
        {
            var a = clip[edge]; var b = clip[(edge + 1) % clip.Length]; var input = points; points = [];
            double Cross(Windows.Foundation.Point p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            var previous = input[^1]; var previousCross = Cross(previous);
            foreach (var current in input)
            {
                var currentCross = Cross(current);
                if ((currentCross >= 0) != (previousCross >= 0)) { var t = previousCross / (previousCross - currentCross); points.Add(new(previous.X + t * (current.X - previous.X), previous.Y + t * (current.Y - previous.Y))); }
                if (currentCross >= 0) points.Add(current); previous = current; previousCross = currentCross;
            }
        }
        var result = new PointCollection(); foreach (var point in points) result.Add(point); return result;
    }
}
