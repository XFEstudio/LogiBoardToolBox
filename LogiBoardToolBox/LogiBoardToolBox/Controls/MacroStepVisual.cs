using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace LogiBoardToolBox.Controls;

internal static class MacroStepVisual
{
    public static FrameworkElement Mouse(int button, bool down, bool showDirection)
    {
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        var canvas = new Canvas { Width = 26, Height = 64 };
        var body = new PathFigure { StartPoint = new(13, 12), IsClosed = true };
        Curve(body, new(17, 12), new(25, 18), new(25, 25));
        body.Segments.Add(new LineSegment { Point = new(25, 38) });
        Curve(body, new(25, 45), new(20, 50), new(13, 50));
        Curve(body, new(6, 50), new(1, 45), new(1, 38));
        body.Segments.Add(new LineSegment { Point = new(1, 25) });
        Curve(body, new(1, 18), new(9, 12), new(13, 12));
        canvas.Children.Add(new Path { Data = Geometry(body), Stroke = white, StrokeThickness = .8 });

        if (button == 3)
        {
            var wheel = new Rectangle { Width = 5, Height = 12, RadiusX = 2.5, RadiusY = 2.5, Fill = white };
            Canvas.SetLeft(wheel, 10.5);
            Canvas.SetTop(wheel, 17);
            canvas.Children.Add(wheel);
        }
        else
        {
            var pad = new PathFigure { StartPoint = new(12, 13.5), IsClosed = true };
            Curve(pad, new(8, 14.5), new(2.5, 20), new(2.5, 25));
            pad.Segments.Add(new LineSegment { Point = new(2.5, 31) });
            Curve(pad, new(6, 31), new(9, 30), new(11.5, 28));
            var highlight = new Path { Data = Geometry(pad), Fill = white };
            if (button == 2) highlight.RenderTransform = new ScaleTransform { CenterX = 13, ScaleX = -1 };
            canvas.Children.Add(highlight);
        }

        if (showDirection)
        {
            var arrow = new Polygon { Fill = white };
            arrow.Points.Add(new Point(6, down ? 55 : 6));
            arrow.Points.Add(new Point(20, down ? 55 : 6));
            arrow.Points.Add(new Point(13, down ? 62 : 0));
            canvas.Children.Add(arrow);
        }
        return canvas;
    }

    public static FrameworkElement Delay(string milliseconds)
    {
        var panel = new StackPanel { MinWidth = 34, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = milliseconds, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Microsoft.UI.Colors.White), Margin = new(0, 1, 0, 0) });
        panel.Children.Add(new TextBlock { Text = "毫秒", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
        return panel;
    }

    private static PathGeometry Geometry(PathFigure figure)
    {
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static void Curve(PathFigure figure, Point first, Point second, Point end)
        => figure.Segments.Add(new BezierSegment { Point1 = first, Point2 = second, Point3 = end });
}
