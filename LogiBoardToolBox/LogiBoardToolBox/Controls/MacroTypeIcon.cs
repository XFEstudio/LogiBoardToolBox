using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace LogiBoardToolBox.Controls;

public sealed class MacroTypeIcon : UserControl
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
            var geometry = Kind == "0" ? "M 3,12 L 21,12 M 15,6 L 21,12 L 15,18" : "M 4,10 C 4,1 21,1 21,12 C 21,18 17,22 12,21 M 12,21 L 16,17 M 12,21 L 17,24";
            var path = (Microsoft.UI.Xaml.Shapes.Path)Microsoft.UI.Xaml.Markup.XamlReader.Load($"<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='{geometry}' Stroke='White' StrokeThickness='1.5'/>"); canvas.Children.Add(path);
        }
        else if (Kind == "2")
        {
            var border = new Border { Width = 24, Height = 14, CornerRadius = new(7), BorderBrush = white, BorderThickness = new(1.5) }; Canvas.SetTop(border, 5); canvas.Children.Add(border);
            var circle = new Ellipse { Width = 11, Height = 11, Fill = white }; Canvas.SetLeft(circle, 11); Canvas.SetTop(circle, 6.5); canvas.Children.Add(circle);
        }
        else
        {
            for (var i = 2; i >= 0; i--) { var rectangle = new Rectangle { Width = 10, Height = 11, RadiusX = 1.5, RadiusY = 1.5, Stroke = white, StrokeThickness = 1.2, Fill = i == 0 ? white : new SolidColorBrush(Microsoft.UI.Colors.Transparent) }; Canvas.SetLeft(rectangle, i * 6); Canvas.SetTop(rectangle, 5 + i * 2); canvas.Children.Add(rectangle); }
        }
    }
}
