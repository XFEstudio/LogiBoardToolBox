namespace LogiBoardToolBox.Views;

public sealed partial class MainPage
{
    private void LuaEditor_Click(object sender, RoutedEventArgs e) => OpenLuaEditor();
    internal void OpenLuaEditor() { StopRecording(); LuaOverlay.Visibility = Visibility.Visible; }
    internal void CloseLuaEditor() => LuaOverlay.Visibility = Visibility.Collapsed;
    private void LuaEditor_Closed(object? sender, EventArgs e) => LuaOverlay.Visibility = Visibility.Collapsed;
    private void LuaMacro_Created(object? sender, EventArgs e)
    {
        LuaOverlay.Visibility = Visibility.Collapsed;
        WorkspaceTabs.SelectedIndex = 1;
        MacroEditorHost.BeginImported(ViewModel.SelectedMacro!);
    }
}
