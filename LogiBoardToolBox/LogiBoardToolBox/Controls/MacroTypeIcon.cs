using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace LogiBoardToolBox.Controls;

public sealed partial class MacroTypeIcon : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(MacroTypeIcon), new PropertyMetadata("0", (d, _) => ((MacroTypeIcon)d).Draw()));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    private readonly Canvas canvas = new() { Width = 24, Height = 24 };
    public MacroTypeIcon() { Content = canvas; Draw(); }
    private void Draw()
    {
        canvas.Children.Clear(); var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        if (Kind is "0" or "1")
        {
            var geometry = new PathGeometry();
            if (Kind == "0")
            {
                geometry.Figures.Add(Lines(new(3, 12), new Point(21, 12)));
                geometry.Figures.Add(Lines(new(15, 6), new(21, 12), new(15, 18)));
            }
            else
            {
                var curve = new PathFigure { StartPoint = new(4, 10) };
                curve.Segments.Add(new BezierSegment { Point1 = new(4, 1), Point2 = new(21, 1), Point3 = new(21, 12) });
                curve.Segments.Add(new BezierSegment { Point1 = new(21, 18), Point2 = new(17, 22), Point3 = new(12, 21) });
                geometry.Figures.Add(curve);
                geometry.Figures.Add(Lines(new(12, 21), new Point(16, 17)));
                geometry.Figures.Add(Lines(new(12, 21), new Point(17, 24)));
            }
            canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = geometry, Stroke = white, StrokeThickness = 1.5 });
        }
        else if (Kind == "2")
        {
            var border = new Border { Width = 24, Height = 14, CornerRadius = new(7), BorderBrush = white, BorderThickness = new(1.5) }; Canvas.SetTop(border, 5); canvas.Children.Add(border);
            var circle = new Ellipse { Width = 11, Height = 11, Fill = white }; Canvas.SetLeft(circle, 11); Canvas.SetTop(circle, 6.5); canvas.Children.Add(circle);
        }
        else if (Kind == "4")
        {
            var loop = new PathFigure { StartPoint = new(12, 12), IsClosed = true };
            loop.Segments.Add(new BezierSegment { Point1 = new(9, 8), Point2 = new(8, 7), Point3 = new(6, 7) });
            loop.Segments.Add(new BezierSegment { Point1 = new(1, 7), Point2 = new(1, 17), Point3 = new(6, 17) });
            loop.Segments.Add(new BezierSegment { Point1 = new(10, 17), Point2 = new(14, 7), Point3 = new(18, 7) });
            loop.Segments.Add(new BezierSegment { Point1 = new(23, 7), Point2 = new(23, 17), Point3 = new(18, 17) });
            loop.Segments.Add(new BezierSegment { Point1 = new(16, 17), Point2 = new(15, 16), Point3 = new(12, 12) });
            var geometry = new PathGeometry();
            geometry.Figures.Add(loop);
            canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = geometry, Stroke = white, StrokeThickness = 1.5 });
        }
        else
        {
            for (var i = 2; i >= 0; i--) { var rectangle = new Rectangle { Width = 10, Height = 11, RadiusX = 1.5, RadiusY = 1.5, Stroke = white, StrokeThickness = 1.2, Fill = i == 0 ? white : new SolidColorBrush(Microsoft.UI.Colors.Transparent) }; Canvas.SetLeft(rectangle, i * 6); Canvas.SetTop(rectangle, 5 + i * 2); canvas.Children.Add(rectangle); }
        }
    }

    private static PathFigure Lines(Point start, params Point[] vertices)
    {
        var figure = new PathFigure { StartPoint = start };
        foreach (var point in vertices) figure.Segments.Add(new LineSegment { Point = point });
        return figure;
    }
}
