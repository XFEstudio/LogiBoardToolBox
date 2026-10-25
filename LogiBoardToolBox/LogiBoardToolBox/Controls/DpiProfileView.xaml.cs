using LogiBoardToolBox.ViewModels;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace LogiBoardToolBox.Controls;

public sealed partial class DpiProfileView : UserControl
{
    public MainPageViewModel ViewModel => App.MouseWorkspace;
    private CancellationTokenSource? pending;
    public event EventHandler? BoardRequested;
    public event EventHandler? HeroRequested;
    public DpiProfileView() { InitializeComponent(); DataContext = ViewModel; CustomPresets.ItemsSource = ViewModel.SensitivityPresets; Unloaded += (_, _) => pending?.Cancel(); }
    private void SearchPresets_Click(object sender, RoutedEventArgs e) { PresetSearch.Visibility = PresetSearch.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; if (PresetSearch.Visibility == Visibility.Visible) PresetSearch.Focus(FocusState.Programmatic); }
    private void PresetSearch_Changed(object sender, TextChangedEventArgs e) => PresetList.ItemsSource = ViewModel.DpiPresets.Where(p => p.Label.Contains(PresetSearch.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
    private async void Preset_Click(object sender, ItemClickEventArgs e) { ViewModel.SelectedDpiPreset = (EditorChoice)e.ClickedItem; ViewModel.ApplyDpiPresetCommand.Execute(null); ViewModel.SensitivityName = ViewModel.SelectedDpiPreset.Label; await ApplyLive(); }
    private async void CustomPreset_Click(object sender, ItemClickEventArgs e) { ViewModel.LoadSensitivityPreset((SensitivityPreset)e.ClickedItem); await ApplyLive(); }
    private void AddPreset_Click(object sender, RoutedEventArgs e) => ViewModel.SaveSensitivityPreset(true);
    private void DeletePreset_Click(object sender, RoutedEventArgs e) => ViewModel.DeleteSensitivityPreset();
    private async void RenamePreset_Click(object sender, RoutedEventArgs e)
    {
        var field = new TextBox { Text = ViewModel.SensitivityName, MaxLength = 64 };
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "为预设命名", Content = field, PrimaryButtonText = "保存", CloseButtonText = "取消" };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(field.Text)) { ViewModel.SensitivityName = field.Text.Trim(); ViewModel.SaveSensitivityPreset(false); }
    }
    private async void Stage_Click(object sender, ItemClickEventArgs e) { ViewModel.SelectedDpiStage = (DpiStageEditor)e.ClickedItem; if (!ViewModel.IsEditingOnboard) await ApplyLive(); }
    private async void DpiSlider_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (sender is not PrecisionSlider { FocusState: not FocusState.Unfocused, Tag: DpiStageEditor stage } slider) return;
        stage.NumericValue = ViewModel.SnapDpi((int)slider.Value); ViewModel.SelectedDpiStage = stage;
        pending?.Cancel(); var delay = pending = new();
        try { await Task.Delay(250, delay.Token); await ApplyLive(); }
        catch (OperationCanceledException) { }
        finally { delay.Dispose(); if (ReferenceEquals(pending, delay)) pending = null; }
    }
    private void DpiValue_Validating(object? sender, TextCommitEventArgs e)
    {
        if (sender is not CommitTextBox { Tag: DpiStageEditor stage } field) return;
        e.Error = ViewModel.ValidateDpiInput(stage, e.Text, Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(field) == "stage-dpi-y");
        ViewModel.DpiInputError = e.Error ?? "";
    }
    private void DpiValue_DraftEdited(object? sender, EventArgs e) => ViewModel.DpiInputError = "";
    private async void DpiValue_Committed(object? sender, EventArgs e) { if (sender is FrameworkElement { Tag: DpiStageEditor stage }) { ViewModel.SelectedDpiStage = stage; await ApplyLive(); } }
    private async void WirelessRate_Closed(object sender, object e) { ViewModel.SelectedLiveRate = ViewModel.LiveReportRates.FirstOrDefault(r => r.Id == ViewModel.SelectedWirelessRate?.Id); await ApplyLive(); }
    private void StageAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { CommandParameter: DpiStageEditor stage } item) return;
        ViewModel.SelectedDpiStage = stage;
        switch (item.Tag?.ToString()) { case "default": ViewModel.SetDpiDefaultCommand.Execute(null); break; case "shift": ViewModel.SetDpiShiftCommand.Execute(null); break; case "disable": stage.Enabled = false; break; }
    }
    private async Task ApplyLive() { if (!Utilities.UiVerification.IsRequested) { ViewModel.UpdateSensitivityPreset(); if (ViewModel.IsEditingOnboard) ViewModel.RequestImmediateSave(); else if (ViewModel.CanApplyPerformance && !ViewModel.IsBusy) await ViewModel.ApplyPerformanceCommand.ExecuteAsync(null); } }
    private void StageProfile_Click(object sender, RoutedEventArgs e) => BoardRequested?.Invoke(this, EventArgs.Empty);
    private void Hero_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (HeroSelector?.SelectedIndex != 1) return;
        HeroSelector.SelectedIndex = 0;
        HeroRequested?.Invoke(this, EventArgs.Empty);
    }
    private async void WindowsMouse_Click(object sender, RoutedEventArgs e) => await Windows.System.Launcher.LaunchUriAsync(new("ms-settings:mousetouchpad"));
}
