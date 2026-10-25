using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LogiBoardToolBox.Controls;

/// <summary>Device sections without a native tab header or its reserved layout row.</summary>
public sealed class WorkspaceHost : Grid
{
    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(WorkspaceHost), new PropertyMetadata(0, Changed));
    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.Register(nameof(IsEnabled), typeof(bool), typeof(WorkspaceHost), new PropertyMetadata(true, Changed));
    public bool IsEnabled { get => (bool)GetValue(IsEnabledProperty); set => SetValue(IsEnabledProperty, value); }
    public event SelectionChangedEventHandler? SelectionChanged;
    public WorkspaceHost() => Loaded += (_, _) => Refresh(true);
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((WorkspaceHost)d).Refresh(e.Property == SelectedIndexProperty);
    private void Refresh(bool notify)
    {
        for (var i = 0; i < Children.Count; i++)
        { Children[i].Visibility = i == SelectedIndex ? Visibility.Visible : Visibility.Collapsed; if (Children[i] is Control control) control.IsEnabled = IsEnabled; }
        if (notify) SelectionChanged?.Invoke(this, new SelectionChangedEventArgs([], []));
    }
}
public sealed class WorkspaceSection : ContentControl
{
    public string Header { get; set; } = "";
    public WorkspaceSection() { HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch; }
}
