using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Shapes;

namespace LogiBoardToolBox.Controls;

/// <summary>Thin G HUB track with a white thumb, while retaining RangeBase keyboard/accessibility semantics.</summary>
public sealed partial class PrecisionSlider : RangeBase
{
    public static readonly DependencyProperty StepFrequencyProperty = DependencyProperty.Register(nameof(StepFrequency), typeof(double), typeof(PrecisionSlider), new PropertyMetadata(1d));
    public double StepFrequency { get => (double)GetValue(StepFrequencyProperty); set => SetValue(StepFrequencyProperty, value); }
    private Grid? track;
    private Rectangle? fill;
    private Ellipse? thumb;
    private bool dragging;
    public event EventHandler<double>? UserValueChanged;
    public PrecisionSlider()
    {
        IsTabStop = true; MinHeight = 28;
        DefaultStyleKey = typeof(PrecisionSlider);
        foreach (var property in new[] { MinimumProperty, MaximumProperty, ValueProperty, IsEnabledProperty }) RegisterPropertyChangedCallback(property, (_, _) => Refresh());
        KeyDown += (_, e) =>
        {
            var next = e.Key switch { Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Down => Value - StepFrequency, Windows.System.VirtualKey.Right or Windows.System.VirtualKey.Up => Value + StepFrequency, Windows.System.VirtualKey.Home => Minimum, Windows.System.VirtualKey.End => Maximum, _ => double.NaN };
            if (!double.IsFinite(next)) return; Value = Math.Clamp(next, Minimum, Maximum); UserValueChanged?.Invoke(this, Value); e.Handled = true;
        };
    }
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate(); track = (Grid)GetTemplateChild("TrackArea"); fill = (Rectangle)GetTemplateChild("ValueFill"); thumb = (Ellipse)GetTemplateChild("Thumb");
        track.SizeChanged += (_, _) => Refresh();
        track.PointerPressed += (_, e) => { Focus(FocusState.Pointer); dragging = true; track.CapturePointer(e.Pointer); SetFromPointer(e.GetCurrentPoint(track).Position.X); e.Handled = true; };
        track.PointerMoved += (_, e) => { if (dragging) SetFromPointer(e.GetCurrentPoint(track).Position.X); };
        track.PointerReleased += (_, e) => { if (dragging) SetFromPointer(e.GetCurrentPoint(track).Position.X); dragging = false; track.ReleasePointerCapture(e.Pointer); };
        track.PointerCaptureLost += (_, _) => dragging = false;
        Refresh();
    }
    private void SetFromPointer(double x)
    {
        var width = Math.Max(1, track!.ActualWidth - 12); var value = Minimum + Math.Clamp((x - 6) / width, 0, 1) * (Maximum - Minimum);
        var step = StepFrequency > 0 ? StepFrequency : 1; Value = Math.Clamp(Minimum + Math.Round((value - Minimum) / step) * step, Minimum, Maximum);
        UserValueChanged?.Invoke(this, Value);
    }
    private void Refresh()
    {
        if (track is null || fill is null || thumb is null) return;
        var width = Math.Max(0, track.ActualWidth - 12); var x = Maximum > Minimum ? Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0, 1) * width : 0;
        fill.Width = x; thumb.Margin = new(x, 0, 0, 0); track.Opacity = IsEnabled ? 1 : .4;
    }
}
