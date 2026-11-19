using System.ComponentModel;
using System.Collections.Specialized;
using LogiBoardToolBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using LogiBoardToolBox.Core.Devices;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LogiBoardToolBox.Controls;

public sealed partial class MouseMapView : UserControl
{
    // Coordinates are normalized to the classic G502 front images. The index is the 0x8100 binding record.
    private static readonly (int Index, double X, double Y, double LabelX, double LabelY)[] ClassicG502Points =
    [
        (0, 372, 220, 416, 242), // G1
        (7, 210, 300, 416, 300), // G8
        (10, 445, 322, 416, 383), // Wheel tilt left
        (6, 210, 366, 416, 411), // G7
        (5, 142, 408, 416, 459), // G6
        (4, 151, 518, 416, 513), // G5
        (3, 198, 639, 416, 566), // G4
        (1, 775, 222, 906, 242), // G2
        (2, 576, 320, 906, 305), // G3
        (9, 708, 322, 906, 382), // Wheel tilt right
        (8, 585, 518, 906, 473), // G9
    ];
    private List<ButtonBindingEditor> observed = [];
    private INotifyCollectionChanged? observedCollection;
    private readonly HashSet<string> failedArtwork = new(StringComparer.Ordinal);
    public static readonly DependencyProperty BindingsProperty = DependencyProperty.Register(nameof(Bindings), typeof(object), typeof(MouseMapView), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty IsSideViewProperty = DependencyProperty.Register(nameof(IsSideView), typeof(bool), typeof(MouseMapView), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty SelectedButtonProperty = DependencyProperty.Register(nameof(SelectedButton), typeof(object), typeof(MouseMapView), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty DeviceModelProperty = DependencyProperty.Register(nameof(DeviceModel), typeof(MouseModel), typeof(MouseMapView), new PropertyMetadata(null, Changed));
    public MouseModel? DeviceModel { get => (MouseModel?)GetValue(DeviceModelProperty); set => SetValue(DeviceModelProperty, value); }
    public object? Bindings { get => GetValue(BindingsProperty); set => SetValue(BindingsProperty, value); }
    public bool IsSideView { get => (bool)GetValue(IsSideViewProperty); set => SetValue(IsSideViewProperty, value); }
    public object? SelectedButton { get => GetValue(SelectedButtonProperty); set => SetValue(SelectedButtonProperty, value); }
    public event EventHandler<ButtonBindingEditor>? ButtonSelected;
    public event EventHandler<MouseAssignmentDrop>? AssignmentDropped;
    public event EventHandler<ButtonBindingEditor>? ResetRequested;
    public event EventHandler<ButtonBindingEditor>? DisableRequested;
    public event EventHandler<ButtonBindingEditor>? EditMacroRequested;
    public MouseMapView()
    {
        InitializeComponent();
        foreach (var image in new[] { TopModelImage, ModelImage, SideView })
        {
            image.ImageOpened += (_, _) => Draw();
            image.ImageFailed += (sender, error) =>
            {
                if (sender is not Image failed || failed.Source is not BitmapImage bitmap || bitmap.UriSource is null) return;
                failedArtwork.Add(bitmap.UriSource.AbsoluteUri);
                Core.Diagnostics.DiagnosticLog.Record("device-artwork", "image failed", new { Model = DeviceModel?.Id, Asset = bitmap.UriSource.AbsoluteUri, error.ErrorMessage });
                Draw();
            };
        }
        Unloaded += (_, _) =>
        {
            Unsubscribe();
            if (observedCollection is not null) observedCollection.CollectionChanged -= CollectionChanged;
            observedCollection = null;
        };
        Loaded += (_, _) => Refresh();
        SizeChanged += (_, _) => Draw();
    }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((MouseMapView)sender).Refresh();
    private void Unsubscribe() { foreach (var row in observed) row.PropertyChanged -= RowChanged; observed.Clear(); }
    private void RowChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(ButtonBindingEditor.DisplayAction)) Draw(); }
    private void Refresh()
    {
        if (Callouts is null) return;
        if (!ReferenceEquals(observedCollection, Bindings))
        {
            if (observedCollection is not null) observedCollection.CollectionChanged -= CollectionChanged;
            observedCollection = Bindings as INotifyCollectionChanged;
            if (observedCollection is not null) observedCollection.CollectionChanged += CollectionChanged;
        }
        Unsubscribe(); observed = (Bindings as IEnumerable<ButtonBindingEditor>)?.ToList() ?? [];
        foreach (var row in observed) row.PropertyChanged += RowChanged;
        Draw();
    }
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();
    private void Draw()
    {
        if (Callouts is null) return;
        Callouts.Children.Clear();
        var labelScale = 1 / Math.Clamp(Math.Min(ActualWidth / 1500, ActualHeight / 1000), .55, 1);
        var model = DeviceModel ?? MouseCatalog.Unknown;
        var originalLayout = model.Layout == "G502X";
        var classicLayout = model.Layout == "G502Classic";
        var hasImage = model.IsKnown && !string.IsNullOrWhiteSpace(model.ImageAsset);
        var sideView = originalLayout && IsSideView && model.HasSideView;
        var asset = sideView ? model.SideImageAsset! : model.ImageAsset;
        var uri = hasImage ? new Uri("ms-appx:///" + asset.TrimStart('/')) : null;
        var available = uri is not null && !failedArtwork.Contains(uri.AbsoluteUri);
        TopView.Visibility = originalLayout && !sideView && available ? Visibility.Visible : Visibility.Collapsed;
        SideView.Visibility = sideView && available ? Visibility.Visible : Visibility.Collapsed;
        ModelImage.Visibility = !originalLayout && available ? Visibility.Visible : Visibility.Collapsed;
        GenericSilhouette.Visibility = !available ? Visibility.Visible : Visibility.Collapsed;
        ArtworkHint.Visibility = !available ? Visibility.Visible : Visibility.Collapsed;
        ArtworkHint.Text = model.IsKnown ? "设备图片未能加载" : "型号待识别 · 通用按键示意";
        if (available)
        {
            var target = originalLayout ? sideView ? SideView : TopModelImage : ModelImage;
            if (target.Source is not BitmapImage current || current.UriSource != uri) target.Source = new BitmapImage(uri);
            ModelImage.Width = 435 * model.ImageAspect;
        }
        // Registration points from the installed G502 X PLUS device metadata.
        (int Index, double X, double Y, double LabelX, double LabelY)[] top =
        [(0,480,450,416,233), (1,1110,450,906,287), (2,800,680,906,233),
         (6,700,680,416,340), (7,900,680,906,340), (8,800,1250,906,448),
         (9,300,500,416,287), (10,250,950,416,394), (12,800,500,750,200), (11,800,850,416,448)];
        (int Index, double X, double Y, double LabelX, double LabelY)[] side =
        [(3,580,1800,420,535), (4,260,1000,420,362), (5,500,1400,420,448)];
        if (originalLayout && !model.HasSideView)
            top = [.. top, (5,210,1160,416,502), (3,205,1420,416,556), (4,100,1680,416,610)];
        var points = originalLayout ? (sideView ? side : top) : classicLayout && available ? ClassicG502Points : ModelPoints(model);
        var imageWidth = originalLayout ? (sideView ? 179.74 : 258.28125) : 435 * model.ImageAspect;
        var imageOriginX = (1500 - imageWidth) / 2;
        foreach (var point in points)
        {
            var row = observed.FirstOrDefault(r => r.Index == point.Index); if (row is null) continue;
            var px = imageOriginX + point.X / (originalLayout ? sideView ? 936 : 1391 : 1000) * imageWidth;
            var py = 254 + point.Y / (originalLayout ? 2700 : 1000) * 435;
            var selected = ReferenceEquals(SelectedButton, row);
            var color = ColorHelper.FromArgb(255, 80, 80, 80);
            var line = new Polyline { Stroke = new SolidColorBrush(color), StrokeThickness = 1.25 };
            var scrollTop = originalLayout && point.Index == 12;
            var fromLeft = point.LabelX < 600;
            if (scrollTop)
            {
                line.Points.Add(new(px, py)); line.Points.Add(new(px, point.LabelY + 21));
            }
            else
            {
                line.Points.Add(new(px, py)); line.Points.Add(new(fromLeft ? 622 : 880, point.LabelY + 21));
                line.Points.Add(new(fromLeft ? point.LabelX : point.LabelX + 180, point.LabelY + 21));
            }
            Callouts.Children.Add(line);
            var marker = new Button { Width = 12 * labelScale, Height = 12 * labelScale, MinWidth = 0, MinHeight = 0, Padding = new(0), CornerRadius = new(6 * labelScale), Background = new SolidColorBrush(Colors.White), BorderThickness = new(2), BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 190, 190, 190)), Tag = row };
            marker.Click += Select; ConfigureDrop(marker); Canvas.SetLeft(marker, px - 6 * labelScale); Canvas.SetTop(marker, py - 6 * labelScale); Callouts.Children.Add(marker);
            var label = new Button { Width = scrollTop ? 130 : 180, Height = 26, MinHeight = 0, HorizontalContentAlignment = scrollTop ? HorizontalAlignment.Center : fromLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new(0), Padding = new(0), Tag = row };
            var display = row.DisplayAction;
            if (model.Layout == "Ambidextrous" && row.Index == 5) display = "底部 · " + display;
            ToolTipService.SetToolTip(marker, row.Label + " · " + display);
            ToolTipService.SetToolTip(label, row.Label + " · " + display);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(marker, row.Label + " · " + display);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(label, row.Label + " · " + display);
            if ((model.Layout == "Compact" && row.Index is 3 or 4) || (originalLayout && !model.HasSideView && row.Index is 3 or 4 or 5) ||
                (classicLayout && row.Index is >= 3 and <= 8))
                display = $"G{row.Index + 1} · {display}";
            if (row.Index is 11 or 12 && display == "未分配") display = row.Index == 11 ? "向下滚动" : "向上滚动";
            var text = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            if (row.SelectedAction.Id == "macro" || Core.Onboard.ButtonBinding.IsMacro(row.Original))
                text.Children.Add(new Border { Width = 18, Height = 18, Background = new SolidColorBrush(ColorHelper.FromArgb(255, 130, 130, 130)), Child = new TextBlock { Text = "M", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Center } });
            text.Children.Add(new TextBlock { Text = display, FontSize = 12 * labelScale, FontFamily = new FontFamily("Arial"), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = new SolidColorBrush(selected ? ColorHelper.FromArgb(255, 255, 210, 0) : Colors.White) });
            if (point.Index is 11 or 12) text.Children.Add(new Border { Width = 11, Height = 11, CornerRadius = new(6), Background = new SolidColorBrush(ColorHelper.FromArgb(255, 70, 70, 70)), Child = new TextBlock { Text = "?", FontSize = 8, Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Center }, VerticalAlignment = VerticalAlignment.Center });
            label.Content = text; label.Click += Select; ConfigureDrop(label); Canvas.SetLeft(label, scrollTop ? px - 65 : point.LabelX); Canvas.SetTop(label, point.LabelY); Callouts.Children.Add(label);
        }
    }
    private (int Index, double X, double Y, double LabelX, double LabelY)[] ModelPoints(MouseModel model)
    {
        var result = new List<(int, double, double, double, double)>
        { (0,280,200,416,233), (1,720,200,906,287), (2,500,220,906,233) };
        if (model.Layout == "Compact" || model.Layout == "Ambidextrous")
        {
            result.Add((3,35,480,416,448)); result.Add((4,35,350,416,340));
            if (model.Layout == "Ambidextrous")
            { result.Add((5,500,960,906,555)); result.Add((6,965,480,906,448)); result.Add((7,965,350,906,340)); }
            else result.Add((5,500,365,906,394));
        }
        else
            for (var i = 3; i < observed.Count; i++)
            {
                var right = (i & 1) == 0; var row = (i - 3) / 2;
                result.Add((i,right ? 930 : 70,350 + row * 110,right ? 906 : 416,340 + row * 65));
            }
        return result.ToArray();
    }
    private void Select(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not ButtonBindingEditor row) return;
        SelectedButton = row; ButtonSelected?.Invoke(this, row);
        var menu = new MenuFlyout();
        if (row.SelectedAction.Id == "macro") { var edit = new MenuFlyoutItem { Text = "编辑宏" }; edit.Click += (_, _) => EditMacroRequested?.Invoke(this, row); menu.Items.Add(edit); }
        var reset = new MenuFlyoutItem { Text = "使用默认值" }; reset.Click += (_, _) => ResetRequested?.Invoke(this, row); menu.Items.Add(reset);
        var disable = new MenuFlyoutItem { Text = "禁用" }; disable.Click += (_, _) => DisableRequested?.Invoke(this, row); menu.Items.Add(disable);
        menu.ShowAt(Callouts.Children.OfType<Button>().FirstOrDefault(b => ReferenceEquals(b.Tag, row)) ?? (FrameworkElement)this);
    }
    private void ConfigureDrop(Button button)
    {
        button.AllowDrop = true;
        button.DragOver += (_, e) => e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.Text) ? DataPackageOperation.Copy : DataPackageOperation.None;
        button.Drop += async (_, e) =>
        {
            try
            {
                if (!e.DataView.Contains(StandardDataFormats.Text) || button.Tag is not ButtonBindingEditor row) return;
                var text = await e.DataView.GetTextAsync();
                if (text.Length <= 2048) AssignmentDropped?.Invoke(this, new(row, text));
            }
            catch (Exception ex) { Console.WriteLine("按键拖放失败：" + ex.Message); }
        };
    }
}

public sealed record MouseAssignmentDrop(ButtonBindingEditor Button, string Data);
