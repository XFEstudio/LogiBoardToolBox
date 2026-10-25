using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LogiBoardToolBox.Core.Onboard;

namespace LogiBoardToolBox.ViewModels;

public sealed class ConfigurationChoice(bool onboard, ushort sector, string label)
{
    public bool Onboard { get; } = onboard;
    public ushort Sector { get; } = sector;
    public string Label { get; } = label;
    public string Icon => Onboard ? "\uE950" : "\uE7F4";
    public string Key => (Onboard ? "board-" : "software-") + Sector;
}

public partial class MainPageViewModel
{
    public ObservableCollection<ConfigurationChoice> Configurations { get; } = [];
    [ObservableProperty] public partial ConfigurationChoice? SelectedConfiguration { get; set; }
    [ObservableProperty] public partial string PersistenceStatus { get; set; } = "";
    public bool IsEditingOnboard => SelectedConfiguration?.Onboard == true;
    public bool IsEditingSoftware => !IsEditingOnboard;
    private ImmediateSaveQueue? immediateSave;
    private int suppressSave;
    private bool shuttingDown;
    private ProfileDraft? softwareWorkspaceDraft;
    private Core.Lighting.LightingSettings? softwareWorkspaceLighting;
    private readonly HashSet<INotifyPropertyChanged> saveObserved = [];
    private sealed class SaveScope(MainPageViewModel vm) : IDisposable
    {
        public void Dispose() => vm.suppressSave--;
    }
    private IDisposable PauseImmediateSave() { suppressSave++; return new SaveScope(this); }
    private void InitializeImmediateSave()
    {
        immediateSave = new(CommitImmediateAsync);
        immediateSave.Failed += (_, error) => { PersistenceStatus = "保存失败 · 更改仍保留在编辑器中"; ShowError(error); };
        PropertyChanged += SaveWorkspaceChanged;
        DpiStages.CollectionChanged += (_, _) => ObserveSaveRows();
        StandardBindings.CollectionChanged += (_, _) => ObserveSaveRows();
        ShiftBindings.CollectionChanged += (_, _) => ObserveSaveRows();
        LightsyncEdited += (_, _) =>
        {
            if (loadingLights || suppressSave != 0 || !IsEditingOnboard) return;
            try
            {
                var settings = BuildLightsyncSettings();
                if (!settings.CanStoreProfile || !BuildSimpleCanStore()) { if (SelectedProfile is { } unsupported) stagedLights.Remove(unsupported.Sector); PersistenceStatus = "此灯效不支持板载保存，未写入鼠标"; return; }
                if (SelectedProfile is { } profile) stagedLights[profile.Sector] = settings;
                RequestImmediateSave();
            }
            catch (Exception error) when (error is FormatException or ArgumentException or OverflowException) { PersistenceStatus = "等待完整的灯效设置"; }
        };
    }
    private void ObserveSaveRows()
    {
        foreach (var row in saveObserved) row.PropertyChanged -= SaveRowChanged;
        saveObserved.Clear();
        foreach (var row in DpiStages.Cast<INotifyPropertyChanged>().Concat(AllBindings))
        { saveObserved.Add(row); row.PropertyChanged += SaveRowChanged; }
    }
    private void SaveRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is DpiStageEditor && e.PropertyName is nameof(DpiStageEditor.Value) or nameof(DpiStageEditor.ValueY) or nameof(DpiStageEditor.Enabled) or nameof(DpiStageEditor.LinkAxes)
            || sender is ButtonBindingEditor && e.PropertyName is nameof(ButtonBindingEditor.SelectedAction) or nameof(ButtonBindingEditor.Value)) RequestImmediateSave();
    }
    private void SaveWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProfileName) or nameof(ProfileEnabled) or nameof(DefaultDpi) or nameof(ShiftDpi) or nameof(SelectedRate) or nameof(SelectedWirelessRate) or nameof(PowerSaveSeconds) or nameof(PowerOffSeconds)) RequestImmediateSave();
    }
    public void RequestImmediateSave()
    {
        if (Utilities.UiVerification.IsRequested && !Utilities.UiVerification.HardwareWritesEnabled || suppressSave != 0 || refreshingProfiles || shuttingDown || !HasSnapshot || !IsEditingOnboard) return;
        PersistenceStatus = "正在保存更改…"; immediateSave?.Request();
    }
    partial void OnSelectedConfigurationChanged(ConfigurationChoice? value)
    {
        OnPropertyChanged(nameof(IsEditingOnboard)); OnPropertyChanged(nameof(IsEditingSoftware));
        PersistenceStatus = value?.Onboard == true ? "板载配置 · 修改后自动保存" : "软件配置";
    }
    private void RebuildConfigurations()
    {
        Configurations.Clear(); Configurations.Add(new(false, 1, "桌面: 默认"));
        foreach (var profile in Profiles) Configurations.Add(new(true, profile.Sector, $"板载配置 {profile.Sector}"));
        SelectedConfiguration = Configurations.FirstOrDefault(c => c.Onboard && snapshot?.Mode == 1 && c.Sector == snapshot.ActiveSector) ?? Configurations[0];
        AllowExperimental = IsEditingOnboard && IsExperimental;
        OnPropertyChanged(nameof(Configurations)); OnPropertyChanged(nameof(SelectedConfiguration));
    }
    public async Task SelectConfigurationAsync(ConfigurationChoice choice)
    {
        if (SelectedConfiguration?.Key == choice.Key || IsBusy) return;
        try { await FlushImmediateSaveAsync(); } catch (IOException error) { ShowError(error); return; }
        await RunAsync(async () =>
        {
            using var scope = PauseImmediateSave();
            await StopRuntimeCoreAsync();
            if (memory is null || snapshot is null) throw new InvalidOperationException("请先读取设备。");
            // A software draft must never be carried over to the same-numbered onboard slot.
            if (IsEditingSoftware) { softwareWorkspaceDraft = CaptureCurrentDraft(); softwareWorkspaceLighting = BuildLightsyncSettings(); }
            drafts.Clear();
            var target = snapshot.GetProfiles().Single(p => p.Sector == choice.Sector);
            refreshingProfiles = true;
            try { SelectedProfile = target; }
            finally { refreshingProfiles = false; }
            SelectedConfiguration = Configurations.Single(c => c.Key == choice.Key);
            if (!choice.Onboard && softwareWorkspaceDraft is { } software)
            {
                drafts[choice.Sector] = software; RestoreDraft(choice.Sector); drafts.Clear();
                if (softwareWorkspaceLighting is { } lighting) LoadLightsyncSettings(lighting);
            }
            AllowExperimental = choice.Onboard && IsExperimental;
            if (choice.Onboard)
            {
                if (!target.Enabled)
                {
                    ProfileEnabled = true;
                    await CommitImmediateCoreAsync();
                }
                else
                {
                    var state = await memory.ActivateProfileAsync(choice.Sector);
                    snapshot.Mode = state.Mode; snapshot.ActiveSector = state.ActiveSector;
                }
            }
            else
            {
                var state = await memory.SetModeAsync(false);
                snapshot.Mode = state.Mode; snapshot.ActiveSector = state.ActiveSector;
            }
            ModeSummary = choice.Onboard ? $"板载模式 · 活动配置 {choice.Sector}" : "主机模式 · 软件配置";
            await RefreshCurrentPerformanceAsync();
            await ReadAdvancedSettingsAsync();
            UpdateCapabilities();
        });
    }
    public async Task FlushImmediateSaveAsync() { if (immediateSave is not null) await immediateSave.FlushAsync(); }
    private async Task CommitImmediateAsync()
    {
        while (IsBusy && !shuttingDown) await Task.Delay(25);
        if (!IsEditingOnboard || memory is null || snapshot is null || SelectedProfile is null) return;
        // The UI remains disabled during the short transaction, so a read-back cannot overwrite a newer gesture.
        IsBusy = true; HasError = false;
        try { await CommitImmediateCoreAsync(); }
        finally { IsBusy = false; }
    }
    private async Task CommitImmediateCoreAsync()
    {
        var plan = PrepareWrite();
        if (plan.Changes.Count == 0) { PersistenceStatus = "已保存到板载"; return; }
        await StopRuntimeCoreAsync();
        PersistenceStatus = "正在写入并校验…";
        var result = await memory!.CommitAsync(plan, BackupDirectory);
        LastBackupPath = result.BackupPath;
        snapshot = await memory.ReadAsync();
        using var scope = PauseImmediateSave();
        foreach (var profile in snapshot.GetProfiles())
        {
            var existing = Profiles.FirstOrDefault(p => p.Sector == profile.Sector);
            existing?.RefreshFrom(profile);
            if (SelectedProfile?.Sector == profile.Sector) SelectedProfile.RefreshFrom(profile);
        }
        stagedLights.Remove(SelectedProfile!.Sector);
        await RefreshCurrentPerformanceAsync();
        PersistenceStatus = BuildSimpleCanStore() ? "已保存到板载 · 读回一致" : "按键/性能已保存 · 当前灯效需软件";
        ModeSummary = $"板载模式 · 活动配置 {snapshot.ActiveSector}";
        StatusTitle = "板载配置已自动保存";
        Status = $"写入并校验 {result.SectorsWritten} 个扇区。";
        UpdateCapabilities(); Log(Status);
    }
}
