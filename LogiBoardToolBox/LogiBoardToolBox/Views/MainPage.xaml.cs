using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Core.Windows;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using System.Text.Json;
using LogiBoardToolBox.Controls;

namespace LogiBoardToolBox.Views;

public sealed partial class MainPage : Page
{
    private MacroRecorder? recorder;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> pendingRecordedSteps = new();
    private int recordingDrainQueued;
    private bool stoppingRecording;
    private readonly System.Diagnostics.Stopwatch previewTime = System.Diagnostics.Stopwatch.StartNew();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? previewTimer;
    private CancellationTokenSource? lightEditDelay;
    private void HostViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DeviceSurface is null) return;
        DeviceSurface.Width = Math.Max(1280, e.NewSize.Width / 1.25); DeviceSurface.Height = Math.Max(720, e.NewSize.Height / 1.25);
    }
    private bool syncingColor;
    private void LightsyncEffect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var box = (ComboBox)sender;
        if (ViewModel.IsBusy || !box.IsDropDownOpen && box.FocusState == FocusState.Unfocused) return;
        if (box.SelectedItem is EditorChoice effect) ViewModel.SelectedLightsyncEffect = effect;
    }
    private void LightsyncSlider_UserChanged(object? sender, double value)
    {
        if (ViewModel.IsBusy || sender is not PrecisionSlider slider) return;
        switch(slider.Tag?.ToString())
        { case "Period": ViewModel.LightsyncPeriod=value; break; case "Brightness": ViewModel.LightsyncBrightness=value; break; case "Gain": ViewModel.AudioGain=value; break; }
    }
    private void LightsyncPeriodNumber_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e)
    { if (!ViewModel.IsBusy && sender.FocusState != FocusState.Unfocused && double.IsFinite(e.NewValue)) ViewModel.LightsyncPeriod=e.NewValue; }
    private void TimeoutInput_Validating(object? sender, TextCommitEventArgs e)
    {
        if (!ushort.TryParse(e.Text, out _)) e.Error = "请输入 0–65535 之间的整数秒数。";
    }
    private void ColorInput_Validating(object? sender, TextCommitEventArgs e)
    {
        try { Core.Lighting.Rgb.Parse(e.Text); }
        catch (FormatException error) { e.Error = error.Message; }
    }
    private double previewPosition, previousPreviewTime, previousAnimationTime;
    private double editorAnimationTime;
    private void ScreenRegion_Selected(object? sender, SampleRegionEditor region) => ViewModel.SelectedSampleRegion = region;
    private void DpiSlider_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // Initial min/max coercion must never overwrite the profile being loaded.
        if (sender is Slider { FocusState: not FocusState.Unfocused, DataContext: DpiStageEditor stage } slider)
        { stage.NumericValue = ViewModel.SnapDpi((int)slider.Value); ViewModel.SelectedDpiStage = stage; }
    }
    private void LightsyncColor_Changed(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (!syncingColor) ViewModel.LightsyncColor = $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";
    }
    private async void Lightsync_Edited(object? sender, EventArgs e)
    {
        if (ViewModel.IsEditingOnboard || !ViewModel.LightsyncRunning) return;
        lightEditDelay?.Cancel(); var delay = lightEditDelay = new();
        try
        {
            await Task.Delay(350, delay.Token);
            if (!ViewModel.IsEditingOnboard && !ViewModel.IsBusy && ViewModel.LightsyncRunning) await ViewModel.ApplyLightsyncCommand.ExecuteAsync(null);
        }
        catch (OperationCanceledException) { }
        finally { delay.Dispose(); if (ReferenceEquals(lightEditDelay, delay)) lightEditDelay = null; }
    }
    private void RecordMacro_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ViewModel.IsRecording) { StopRecording(); return; }
            if (ViewModel.SelectedMacro is null) throw new InvalidOperationException("请先创建或选择一个宏。");
            if (recorder is null)
            {
                recorder = new MacroRecorder();
                recorder.StepRecorded += (_, step) =>
                {
                    Core.Diagnostics.DiagnosticLog.Record("recorder", "step", new { Step = step });
                    pendingRecordedSteps.Enqueue(step);
                    if (Interlocked.Exchange(ref recordingDrainQueued, 1) == 0) DispatcherQueue.TryEnqueue(DrainRecordedSteps);
                };
            }
            recorder.Start(WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow), MacroEditorHost.IsStopRecordingTarget);
            ViewModel.IsRecording = true;
            Core.Diagnostics.DiagnosticLog.Record("recorder", "started", new { ViewModel.SelectedMacro.Name, ViewModel.SelectedMacro.UseStandardDelay });
        }
        catch (Exception ex) { StopRecording(); ViewModel.ShowError(ex); }
    }
    private void AppendRecordedStep(string step)
    {
                    if (!ViewModel.IsRecording) return;
                    if (step.StartsWith("delay ") && ViewModel.SelectedMacro?.UseStandardDelay == true) return;
                    var limitReached = !stoppingRecording && ViewModel.Timeline.Count >= 5000;
                    var row = MacroStepEditor.FromScript(step);
                    ViewModel.Timeline.Add(row); ViewModel.SelectedStep = row;
                    if (limitReached) { StopRecording(); ViewModel.ShowError(new InvalidOperationException("录制已达到步骤上限，已保留当前步骤并补齐松开。")); }
    }
    private void DrainRecordedSteps()
    {
        Interlocked.Exchange(ref recordingDrainQueued, 0);
        while (pendingRecordedSteps.TryDequeue(out var step)) AppendRecordedStep(step);
    }
    private void StopRecording()
    {
        if (stoppingRecording) return;
        stoppingRecording = true;
        try { var wasRecording = ViewModel.IsRecording; recorder?.Stop(); DrainRecordedSteps(); ViewModel.IsRecording = false; if (wasRecording) Core.Diagnostics.DiagnosticLog.Record("recorder", "stopped"); }
        finally { stoppingRecording = false; }
    }
    private void Assignment_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AssignmentItem item) OpenAssignmentItem(item);
    }
    internal void OpenAssignmentItem(AssignmentItem item)
    {
        if (item.Category != "macros") { ViewModel.AssignPaletteItemCommand.Execute(item); return; }
        if (item.MacroIndex < 0 || item.MacroIndex >= ViewModel.Macros.Count ||
            ViewModel.Macros[item.MacroIndex].Name != item.Value)
        {
            ViewModel.ShowError(new InvalidOperationException("宏列表已更新，请重新选择。"));
            return;
        }
        ViewModel.SelectedMacro = ViewModel.Macros[item.MacroIndex];
        WorkspaceTabs.SelectedIndex = 1;
    }
    private void Assignment_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is not AssignmentItem item) return;
        e.Data.SetText(JsonSerializer.Serialize(item, LogiBoardToolBox.Profiles.ProfileJsonContext.Default.AssignmentItem)); e.Data.RequestedOperation = DataPackageOperation.Copy;
    }
    private void MouseMap_AssignmentDropped(object? sender, MouseAssignmentDrop drop)
    {
        try
        {
            var received = JsonSerializer.Deserialize(drop.Data, LogiBoardToolBox.Profiles.ProfileJsonContext.Default.AssignmentItem);
            var item = ViewModel.AssignmentItems.FirstOrDefault(candidate => candidate == received);
            if (item is null) throw new FormatException("请从功能列表中拖动命令或宏。");
            ViewModel.SelectedButton = drop.Button; ViewModel.AssignPaletteItemCommand.Execute(item);
        }
        catch (Exception ex) { ViewModel.ShowError(ex); }
    }
    private void MouseMap_ButtonSelected(object? sender, ButtonBindingEditor row) => ViewModel.SelectedButton = row;
    private void MouseView_Click(object sender, RoutedEventArgs e) { ViewModel.IsSideView = ((FrameworkElement)sender).Tag?.ToString() == "side"; if (sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton button) button.IsChecked = true; }
    private async void DevicePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is not LogitechDevice device || ViewModel.IsBusy) return;
        await ViewModel.SelectDeviceAsync(device);
        ((ComboBox)sender).SelectedItem = ViewModel.SelectedDevice;
    }
    private async void DeviceSection_Click(object sender, RoutedEventArgs e)
    {
        WorkspaceTabs.SelectedIndex = int.Parse(((FrameworkElement)sender).Tag.ToString()!);
        if (WorkspaceTabs.SelectedIndex == 3 && !ViewModel.IsEditingOnboard && !Utilities.UiVerification.IsRequested && ViewModel.HasSnapshot && !ViewModel.LightsyncRunning) await ViewModel.ApplyLightsyncCommand.ExecuteAsync(null);
    }
    private void DeviceBack_Click(object sender, RoutedEventArgs e) => WorkspaceTabs.SelectedIndex = 4;
    private void AssignmentCategory_Click(object sender, RoutedEventArgs e) => ViewModel.SelectedAssignmentCategory = ViewModel.AssignmentCategories.Single(c => c.Id == ((FrameworkElement)sender).Tag.ToString());
    private void UpdateCategoryButtons()
    {
        foreach (var button in new[] { CommandsCategory, KeysCategory, ActionsCategory, MacrosCategory, SystemCategory })
        {
            var selected = button.Tag.ToString() == ViewModel.SelectedAssignmentCategory?.Id;
            button.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(selected ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(255, 136, 136, 136));
            button.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(selected ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Transparent);
        }
    }
    private void CreateMacro_Click(object sender, RoutedEventArgs e) => BeginNewMacro();
    internal void BeginNewMacro() { WorkspaceTabs.SelectedIndex = 1; MacroEditorHost.BeginCreate(); }
    internal void AdvanceNewMacroName() => MacroEditorHost.AdvanceName();
    internal void ChooseNewMacroType(int type) => MacroEditorHost.ChooseType(type);
    internal void CancelNewMacro() => MacroEditorHost.CancelWithoutPrompt();
    private void MacroEditor_Closed(object? sender, EventArgs e) { StopRecording(); WorkspaceTabs.SelectedIndex = 0; }
    private void MacroEditor_RecordRequested(object? sender, EventArgs e) => RecordMacro_Click(this, new RoutedEventArgs());
    private void BoardRequested(object? sender, EventArgs e) => WorkspaceTabs.SelectedIndex = 4;
    private void OpenBoard_Click(object sender, RoutedEventArgs e) => WorkspaceTabs.SelectedIndex = 4;
    private void SettingsBack_Click(object sender, RoutedEventArgs e) => WorkspaceTabs.SelectedIndex = 0;
    private async void BoardProfile_Click(object sender, ItemClickEventArgs e) { await ViewModel.SelectConfigurationAsync(ViewModel.Configurations.Single(c => c.Onboard && c.Sector == ((MouseProfile)e.ClickedItem).Sector)); WorkspaceTabs.SelectedIndex = 0; }
    private async void OnboardModeButton_Click(object sender, RoutedEventArgs e) => await ViewModel.SelectConfigurationAsync(ViewModel.Configurations.First(c => c.Onboard != ViewModel.OnboardMode));
    private async void Configuration_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ConfigurationPicker.SelectedItem is ConfigurationChoice choice && choice.Key != ViewModel.SelectedConfiguration?.Key) { await ViewModel.SelectConfigurationAsync(choice); ConfigurationPicker.SelectedItem = ViewModel.SelectedConfiguration; }
    }
    private void AnimationPreset_Changed(object sender, SelectionChangedEventArgs e) { if (ViewModel.IsAnimation && !ViewModel.AnimationEditing) ViewModel.LoadAnimationPresetCommand.Execute(null); }
    private void NewAnimation_Click(object sender, RoutedEventArgs e) => OpenAnimationEditor(true);
    private void EditAnimation_Click(object sender, RoutedEventArgs e) => OpenAnimationEditor();
    internal void OpenAnimationEditor(bool create = false)
    {
        try { ViewModel.BeginAnimationEdit(create); AnimationOverlay.Visibility = Visibility.Visible; AnimationEditorHost.RefreshFrames(); }
        catch (Exception error) { ViewModel.ShowError(error); }
    }
    private void DeleteAnimation_Click(object sender, RoutedEventArgs e) { try { ViewModel.DeleteSavedAnimation(); } catch (Exception error) { ViewModel.ShowError(error); } }
    private void AnimationEditor_Closed(object? sender, EventArgs e) => AnimationOverlay.Visibility = Visibility.Collapsed;
    private void HeroRequested(object? sender, EventArgs e) => OpenHeroEditor();
    private void HeroSettings_Click(object sender, RoutedEventArgs e) => OpenHeroEditor();
    internal void OpenHeroEditor() { HeroEditorHost.Begin(); HeroOverlay.Visibility = Visibility.Visible; }
    internal string HeroEditorState => HeroEditorHost.State;
    private void HeroEditor_Closed(object? sender, EventArgs e) => HeroOverlay.Visibility = Visibility.Collapsed;
    private async void WindowsLighting_Click(object sender, RoutedEventArgs e) => await Windows.System.Launcher.LaunchUriAsync(new("ms-settings:personalization-lighting"));
    private void LightsyncMode_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedLightsyncMode = ViewModel.LightsyncModes.Single(m => m.Id == ((FrameworkElement)sender).Tag.ToString());
        if (ViewModel.IsAnimation) ViewModel.LoadAnimationPresetCommand.Execute(null);
    }
    private void UpdateLightTabs()
    {
        foreach (var button in new[] { PresetLightTab, FreestyleLightTab, AnimationLightTab }) { var selected = button.Tag.ToString() == ViewModel.SelectedLightsyncMode.Id; button.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(selected ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(255, 136, 136, 136)); button.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(selected ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Transparent); }
    }
    private void MouseMap_ResetRequested(object? sender, ButtonBindingEditor row) { ViewModel.SelectedButton = row; ViewModel.ResetSelectedButtonCommand.Execute(null); }
    private void MouseMap_DisableRequested(object? sender, ButtonBindingEditor row) { ViewModel.SelectedButton = row; ViewModel.DisableSelectedButtonCommand.Execute(null); }
    private void MouseMap_EditMacroRequested(object? sender, ButtonBindingEditor row)
    {
        try { ViewModel.SelectedMacro = ViewModel.GetUniqueMacroForBinding(row.Value); WorkspaceTabs.SelectedIndex = 1; }
        catch (InvalidOperationException error) { ViewModel.ShowError(error); }
    }
    private void WorkspaceTabs_SelectionChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, WorkspaceTabs) || AssignmentsNav is null) return;
        if (WorkspaceTabs.SelectedIndex == 3) ViewModel.RefreshLightsyncBindings();
        if (MacroOverlay is not null) { MacroOverlay.Visibility = WorkspaceTabs.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed; if (WorkspaceTabs.SelectedIndex == 1) MacroEditorHost.BeginEdit(); else StopRecording(); }
        if (SettingsOverlay is not null) SettingsOverlay.Visibility = WorkspaceTabs.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { AssignmentsNav, PerformanceNav, LightsyncNav })
        {
            var selected = int.Parse(button.Tag.ToString()!) == WorkspaceTabs.SelectedIndex;
            button.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(selected ? Windows.UI.Color.FromArgb(255, 0, 158, 255) : Microsoft.UI.Colors.Transparent);
            button.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(selected ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(255, 153, 153, 153));
        }
    }
    public static MainPage? Current { get; private set; }
    public MainPageViewModel ViewModel => App.MouseWorkspace;
    public MainPage()
    {
        Current = this;
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.AttachDispatcher(DispatcherQueue);
        ViewModel.LuaMacroCreated += LuaMacro_Created;
        DeviceHeader.Translation = new(0, 8, 0);
        App.MainWindow.SetTitleBar(WindowDragRegion);
        Unloaded += (_, _) => { StopRecording(); previewTimer?.Stop(); lightEditDelay?.Cancel(); ViewModel.PropertyChanged -= ViewModel_PropertyChanged; ViewModel.LightsyncEdited -= Lightsync_Edited; };
    }
    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.LightsyncEdited -= Lightsync_Edited; ViewModel.LightsyncEdited += Lightsync_Edited;
        previewTimer ??= DispatcherQueue.CreateTimer(); previewTimer.Interval = TimeSpan.FromMilliseconds(50);
        previewTimer.Tick -= Preview_Tick; previewTimer.Tick += Preview_Tick; previewTimer.Start();
        if (Utilities.UiVerification.IsCatalogRequested)
        {
            UpdateCategoryButtons();
            UpdateLightTabs();
            await Utilities.UiVerification.RunCatalogReviewAsync(this, WorkspaceTabs);
            return;
        }
        await ViewModel.InitializeAsync();
        UpdateCategoryButtons();
        UpdateLightTabs();
        if (!Utilities.UiVerification.IsRequested && !Environment.GetCommandLineArgs().Contains("--background-start")) Focus(FocusState.Programmatic);
        UpdateStatusSeverity();
        if (Utilities.UiVerification.IsInputEditRequested) await Utilities.UiVerification.RunInputEditReviewAsync(this, WorkspaceTabs);
        else if (Utilities.UiVerification.IsRequested) await Utilities.UiVerification.RunAsync(this, WorkspaceTabs);
        await Utilities.UiVerification.RunImportReviewAsync(this);
    }
    private void Preview_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        var now = previewTime.Elapsed.TotalMilliseconds;
        if (ViewModel.PlayLightsyncPreview) previewPosition += now - previousPreviewTime;
        previousPreviewTime = now;
        if (ViewModel.AnimationEditing)
        {
            if (ViewModel.PlayLightsyncPreview)
            {
                editorAnimationTime += now - previousAnimationTime;
                var period = ViewModel.LightsyncPeriod;
                var phase = editorAnimationTime % (ViewModel.SelectedAnimationDirection == Core.Lighting.AnimationDirection.Bounce ? 2 * period : period);
                ViewModel.AnimationPlayhead = phase <= period ? phase : 2 * period - phase;
                ViewModel.UpdateLightPreview(editorAnimationTime);
            }
        }
        else if (WorkspaceTabs.SelectedIndex == 3) ViewModel.UpdateLightPreview(previewPosition);
        previousAnimationTime = now;
    }
    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.HasError)) UpdateStatusSeverity();
        if (e.PropertyName == nameof(ViewModel.PlayLightsyncPreview) && ViewModel.PlayLightsyncPreview) editorAnimationTime = ViewModel.AnimationPlayhead;
        if (e.PropertyName == nameof(ViewModel.SelectedAssignmentCategory)) UpdateCategoryButtons();
        if (e.PropertyName == nameof(ViewModel.SelectedLightsyncMode)) UpdateLightTabs();
        if (e.PropertyName is nameof(ViewModel.SelectedMacro) or nameof(ViewModel.SelectedMacroSegment)) StopRecording();
        if (e.PropertyName == nameof(ViewModel.LightsyncColor))
        {
            try
            {
                var c = Core.Lighting.Rgb.Parse(ViewModel.LightsyncColor); syncingColor = true;
                LightsyncColorPicker.Color = Windows.UI.Color.FromArgb(255, c.R, c.G, c.B);
            }
            catch (FormatException) { }
            finally { syncingColor = false; }
        }
    }
    private async void OnboardMode_Toggled(object sender, RoutedEventArgs e)
    {
        var toggle = (ToggleSwitch)sender;
        if (toggle.IsOn == ViewModel.OnboardMode || !ViewModel.HasSnapshot || ViewModel.IsBusy) return;
        await ViewModel.SetDeviceModeAsync(toggle.IsOn);
        toggle.IsOn = ViewModel.OnboardMode;
    }
    private void UpdateStatusSeverity()
    {
        if (FindStatusBar(Content) is { } status) status.Severity = ViewModel.HasError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
    }
    private static InfoBar? FindStatusBar(object root) => root is Grid grid ? grid.Children.OfType<InfoBar>().FirstOrDefault() : null;
    private async Task ConfirmAndCommitAsync(MemoryWritePlan plan)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "写入板载存储",
            PrimaryButtonText = "写入鼠标",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock { Text = plan.Summary, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.CommitAsync(plan);
    }
    private async void Write_Click(object sender, RoutedEventArgs e)
    {
        try { await ConfirmAndCommitAsync(ViewModel.PrepareWrite()); }
        catch (Exception ex) { ViewModel.ShowError(ex); }
    }
    private void InitializePicker(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
    private async void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = $"LogiBoard-{DateTime.Now:yyyyMMdd-HHmmss}" };
            picker.FileTypeChoices.Add("板载存储备份", new List<string> { ".json" });
            InitializePicker(picker);
            if (await picker.PickSaveFileAsync() is { } file) await ViewModel.ExportBackupAsync(file.Path);
        }
        catch (Exception ex) { ViewModel.ShowError(ex); }
    }
    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json"); InitializePicker(picker);
            if (await picker.PickSingleFileAsync() is not { } file) return;
            MemoryWritePlan? plan = null;
            await ViewModel.RunAsync(async () => plan = await ViewModel.PrepareRestoreAsync(file.Path));
            if (plan is not null) await ConfirmAndCommitAsync(plan);
        }
        catch (Exception ex) { ViewModel.ShowError(ex); }
    }
    private async void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ViewModel.BackupDirectory);
            await Windows.System.Launcher.LaunchFolderPathAsync(ViewModel.BackupDirectory);
        }
        catch (Exception ex) { ViewModel.ShowError(ex); }
    }
}
