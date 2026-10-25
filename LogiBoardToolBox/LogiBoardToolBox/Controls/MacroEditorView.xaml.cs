using System.ComponentModel;
using LogiBoardToolBox.ViewModels;
using Microsoft.UI.Xaml.Media;

namespace LogiBoardToolBox.Controls;

public sealed partial class MacroEditorView : UserControl
{
    public MainPageViewModel ViewModel => App.MouseWorkspace;
    public event EventHandler? Closed;
    public event EventHandler? RecordRequested;
    private MacroEditor? previous, created;
    private MacroDraft? original;
    private int stage;
    private sealed record MacroDraft(string Name, string Script, string Hold, string Release, EditorChoice Repeat, bool Standard, string Delay, bool ShowUpDown, string Color)
    {
        public static MacroDraft Read(MacroEditor macro) => new(macro.Name, macro.Script, macro.HoldScript, macro.ReleaseScript, macro.SelectedRepeat, macro.UseStandardDelay, macro.StandardDelay, macro.ShowUpDown, macro.Color);
        public void Restore(MacroEditor macro) { macro.Name = Name; macro.Script = Script; macro.HoldScript = Hold; macro.ReleaseScript = Release; macro.SelectedRepeat = Repeat; macro.UseStandardDelay = Standard; macro.StandardDelay = Delay; macro.ShowUpDown = ShowUpDown; macro.Color = Color; }
    }
    public MacroEditorView()
    {
        InitializeComponent(); DataContext = ViewModel;
        Loaded += (_, _) => { ViewModel.PropertyChanged += Changed; WatchMacro(null, ViewModel.SelectedMacro); };
        Unloaded += (_, _) => { ViewModel.PropertyChanged -= Changed; WatchMacro(watched, null); };
    }
    private MacroEditor? watched;
    private void WatchMacro(MacroEditor? old, MacroEditor? next) { if (old is not null) old.PropertyChanged -= MacroChanged; watched = next; if (next is not null) next.PropertyChanged += MacroChanged; UpdateTypes(); }
    private void Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(ViewModel.SelectedMacro)) WatchMacro(watched, ViewModel.SelectedMacro); }
    private void MacroChanged(object? sender, PropertyChangedEventArgs e) => UpdateTypes();
    private void UpdateTypes()
    {
        foreach (var button in new[] { OnceType, RepeatType, ToggleType, SequenceType }) { var selected = button.Tag.ToString() == ViewModel.SelectedMacro?.SelectedRepeat.Id; button.BorderBrush = new SolidColorBrush(selected ? Windows.UI.Color.FromArgb(255, 0, 169, 224) : Windows.UI.Color.FromArgb(255, 68, 68, 68)); button.Background = new SolidColorBrush(selected ? Windows.UI.Color.FromArgb(255, 0, 42, 54) : Windows.UI.Color.FromArgb(255, 24, 24, 24)); }
        var current = ViewModel.SelectedMacro;
        SaveButton.IsEnabled = current is not null && !string.IsNullOrWhiteSpace(current.Name) && (!string.IsNullOrWhiteSpace(current.Script) || !string.IsNullOrWhiteSpace(current.HoldScript) || !string.IsNullOrWhiteSpace(current.ReleaseScript)) && (original is null || original != MacroDraft.Read(current));
        ResetButton.IsEnabled = current is not null && (original is null ? !string.IsNullOrEmpty(current.Script + current.HoldScript + current.ReleaseScript) : original != MacroDraft.Read(current));
    }
    public void BeginEdit() { stage = 0; created = null; previous = ViewModel.SelectedMacro; original = previous is null ? null : MacroDraft.Read(previous); ShowStage(); }
    public void BeginCreate()
    {
        previous = ViewModel.SelectedMacro; ViewModel.AddMacroCommand.Execute(null); created = ViewModel.SelectedMacro!;
        created.Name = ""; created.Script = ""; created.HoldScript = ""; created.ReleaseScript = ""; created.UseStandardDelay = true;
        original = null; stage = 1; ShowStage(); if (!Utilities.UiVerification.IsRequested) MacroNameField.Focus(FocusState.Programmatic);
    }
    public void AdvanceName() { if (string.IsNullOrWhiteSpace(ViewModel.SelectedMacro?.Name)) return; ViewModel.SelectedMacro.Name = ViewModel.SelectedMacro.Name.Trim(); stage = 2; ShowStage(); }
    public void ChooseType(int type) { ViewModel.SelectedMacro!.SelectedRepeat = MacroEditor.RepeatChoices[type]; stage = 0; ShowStage(); }
    private void ShowStage() { NamingPanel.Visibility = stage == 1 ? Visibility.Visible : Visibility.Collapsed; TypePanel.Visibility = stage == 2 ? Visibility.Visible : Visibility.Collapsed; EditorPanel.Visibility = stage == 0 ? Visibility.Visible : Visibility.Collapsed; UpdateTypes(); }
    public void CancelWithoutPrompt()
    {
        if (created is not null) { ViewModel.SelectedMacro = created; ViewModel.RemoveMacroCommand.Execute(null); ViewModel.SelectedMacro = previous; created = null; }
        else if (original is not null && ViewModel.SelectedMacro is { } macro) original.Restore(macro);
        Closed?.Invoke(this, EventArgs.Empty);
    }
    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (stage != 0 || ViewModel.SelectedMacro is not { } macro || original == MacroDraft.Read(macro)) { CancelWithoutPrompt(); return; }
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "是否保存对宏的更改？", PrimaryButtonText = "是", SecondaryButtonText = "否", CloseButtonText = "取消" };
        switch (await dialog.ShowAsync()) { case ContentDialogResult.Primary: await Save(); break; case ContentDialogResult.Secondary: CancelWithoutPrompt(); break; }
    }
    private void MacroName_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e) { if (e.Key == Windows.System.VirtualKey.Enter) { AdvanceName(); e.Handled = true; } }
    private void ChooseType_Click(object sender, RoutedEventArgs e) => ChooseType(int.Parse(((Button)sender).Tag.ToString()!));
    private void Type_Click(object sender, RoutedEventArgs e) { if (ViewModel.SelectedMacro is { } macro) macro.SelectedRepeat = MacroEditor.RepeatChoices[int.Parse(((Button)sender).Tag.ToString()!)]; }
    private void Phase_RecordRequested(object? sender, EventArgs e) => RecordRequested?.Invoke(this, EventArgs.Empty);
    private void Record_Click(object sender, RoutedEventArgs e) => RecordRequested?.Invoke(this, EventArgs.Empty);
    private void MacroColor_Changed(ColorPicker sender, ColorChangedEventArgs e) { if (ViewModel.SelectedMacro is { } macro) macro.Color = $"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}"; }
    private void Reset_Click(object sender, RoutedEventArgs e) { if (original is not null && ViewModel.SelectedMacro is { } macro) original.Restore(macro); else if (created is not null) { created.Script = ""; created.HoldScript = ""; created.ReleaseScript = ""; } }
    private async void Save_Click(object sender, RoutedEventArgs e) => await Save();
    private async Task Save()
    {
        if (original is { } before && ViewModel.SelectedMacro is { } current && before.Name != current.Name)
            foreach (var row in ViewModel.AllBindings.Where(r => r.SelectedAction.Id == "macro" && r.Value == before.Name)) { row.Value = current.Name; row.FriendlyName = current.Name; }
        await ViewModel.SaveMacrosCommand.ExecuteAsync(null);
        await ViewModel.FlushImmediateSaveAsync();
        if (!ViewModel.HasError) { created = null; original = null; Closed?.Invoke(this, EventArgs.Empty); }
    }
}
