using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Core.Diagnostics;

namespace LogiBoardToolBox.ViewModels;

public sealed class ConfigurationChoice(bool onboard, ushort sector, string label)
{
    public bool Onboard { get; } = onboard;
    public ushort Sector { get; } = sector;
    public string Label { get; } = label;
    public bool IsSoftware => !Onboard;
    public string Key => (Onboard ? "board-" : "software-") + Sector;
}

public partial class MainPageViewModel
{
    public ObservableCollection<ConfigurationChoice> Configurations { get; } = [];
    [ObservableProperty] public partial ConfigurationChoice? SelectedConfiguration { get; set; }
    [ObservableProperty] public partial string PersistenceStatus { get; set; } = "";
    [ObservableProperty] public partial bool OnboardSaveFailed { get; set; }
    public bool IsEditingOnboard => SelectedConfiguration?.Onboard == true;
    public bool IsEditingSoftware => !IsEditingOnboard;
    public bool CanRetryOnboardSave => IsIdle && IsEditingOnboard && HasSnapshot;
    private bool refreshBeforeRetry;
    private bool userRequestedRetry;
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
        immediateSave.Failed += (_, error) =>
        {
            refreshBeforeRetry = true;
            OnboardSaveFailed = true;
            PersistenceStatus = "保存失败 · 已保留更改";
            ShowError(error, "板载保存失败");
        };
        PropertyChanged += SaveWorkspaceChanged;
        DpiStages.CollectionChanged += (_, _) => ObserveSaveRows();
        StandardBindings.CollectionChanged += (_, _) => ObserveSaveRows();
        ShiftBindings.CollectionChanged += (_, _) => ObserveSaveRows();
        LightsyncEdited += (_, _) =>
        {
            if (loadingLights || suppressSave != 0 || !IsEditingOnboard || SelectedProfile is not { } profile) return;
            try
            {
                var settings = BuildLightsyncSettings();
                if (!settings.CanStoreProfile || !CanStoreLightsyncPreset(settings.Effect, settings.Brightness))
                {
                    stagedLights.Remove(profile.Sector);
                    PersistenceStatus = "当前灯效需软件配置 · 已有板载灯效保持原值";
                    return;
                }
                stagedLights[profile.Sector] = settings;
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
            || sender is ButtonBindingEditor && e.PropertyName is nameof(ButtonBindingEditor.SelectedAction) or nameof(ButtonBindingEditor.Value))
        {
            if (suppressSave == 0)
                DiagnosticLog.Record("edit", e.PropertyName ?? "row", sender is DpiStageEditor d
                    ? (object)new { d.Index, d.Value, d.ValueY, d.Enabled, d.LinkAxes }
                    : sender is ButtonBindingEditor b ? new { b.Index, b.GShift, Action = b.SelectedAction.Id, b.Value } : null);
            RequestImmediateSave();
        }
    }
    private void SaveWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProfileName) or nameof(ProfileEnabled) or nameof(DefaultDpi) or nameof(ShiftDpi) or nameof(SelectedRate) or nameof(SelectedWirelessRate) or nameof(PowerSaveSeconds) or nameof(PowerOffSeconds))
        {
            if (suppressSave == 0) DiagnosticLog.Record("edit", e.PropertyName, new { ProfileName, ProfileEnabled, DefaultDpi, ShiftDpi, Rate = SelectedRate?.Id, WirelessRate = SelectedWirelessRate?.Id, PowerSaveSeconds, PowerOffSeconds });
            RequestImmediateSave();
        }
    }
    public void RequestImmediateSave()
    {
        if (Utilities.UiVerification.IsRequested && !Utilities.UiVerification.HardwareWritesEnabled && verificationStorage is null || suppressSave != 0 || refreshingProfiles || shuttingDown || !HasSnapshot || !IsEditingOnboard) return;
        PersistenceStatus = "正在保存更改…"; immediateSave?.Request();
    }
    partial void OnSelectedConfigurationChanged(ConfigurationChoice? value)
    {
        OnPropertyChanged(nameof(IsEditingOnboard)); OnPropertyChanged(nameof(IsEditingSoftware));
        OnPropertyChanged(nameof(CanRetryOnboardSave));
        OnPropertyChanged(nameof(MacroTypeDescription)); OnPropertyChanged(nameof(ShowSoftwareToggleHint)); OnPropertyChanged(nameof(ShowMacroLoopHint));
        PersistenceStatus = value?.Onboard == true ? "板载配置 · 修改后自动保存" : "软件配置";
        UpdateCurrentDpiStage();
        RefreshProfileRouteLabels();
    }
    private void RebuildConfigurations()
    {
        Configurations.Clear(); Configurations.Add(new(false, 1, "桌面: 默认"));
        foreach (var profile in Profiles) Configurations.Add(new(true, profile.Sector, $"板载配置 {profile.Sector}"));
        SelectedConfiguration = Configurations.FirstOrDefault(c => c.Onboard && snapshot?.Mode == 1 && c.Sector == snapshot.ActiveSector) ?? Configurations[0];
        AllowExperimental = IsEditingOnboard && IsExperimental;
        OnPropertyChanged(nameof(Configurations)); OnPropertyChanged(nameof(SelectedConfiguration));
        RefreshProfilePairChoices();
    }
    public async Task SelectConfigurationAsync(ConfigurationChoice choice)
    {
        if (SelectedConfiguration?.Key == choice.Key || IsBusy) return;
        if (choice.Onboard && ProfilePairActive && CurrentProfileRoute is { } route && choice.Sector != route.FirstProfile && choice.Sector != route.SecondProfile)
        { ShowError(new InvalidOperationException("双配置直达期间仅启用两个所选槽位。请先解除直达，再启用其他槽位。")); return; }
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
            if (!choice.Onboard) MatchStoredMacros(forSoftware: true);
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

    public async Task RetryOnboardSaveAsync()
    {
        if (!CanRetryOnboardSave) return;
        refreshBeforeRetry = true;
        userRequestedRetry = true;
        DiagnosticLog.Record("workspace", "retry onboard save requested");
        RequestImmediateSave();
        try { await FlushImmediateSaveAsync(); }
        catch (Exception error) { DiagnosticLog.Record("workspace", "retry onboard save failed", error: error); }
    }

    private async Task RefreshSnapshotKeepingEditsAsync(bool allowProfileRebase = false)
    {
        if (memory is null || snapshot is null) throw new InvalidOperationException("请先读取设备。");
        PersistenceStatus = "正在重新读取板载数据 · 保留编辑内容…";
        if (allowProfileRebase && client?.HasConnectionError == true && verificationStorage is null &&
            SelectedDevice is { } device && device.Identity == snapshot.DeviceIdentity)
        {
            var replacement = Core.Windows.LogitechDiscovery.Open(device);
            try
            {
                var confirmed = await Core.Devices.HidPlusPlusDeviceProbe.ProbeAsync(replacement, device.Hid);
                if (confirmed?.Identity != device.Identity || !confirmed.HasOnboardProfiles)
                    throw new InvalidOperationException("无法确认仍是同一只鼠标。请重新连接后再重试。");
                var reconnected = new OnboardMemory(replacement, device.Identity, device.Name);
                var reconnectedSnapshot = await reconnected.ReadAsync();
                if (!reconnectedSnapshot.Description.Take(11).SequenceEqual(snapshot.Description.Take(11)))
                    throw new InvalidOperationException("板载布局已改变。请先导出诊断包，再重新读取设备。");
                SetDeviceClient(replacement);
                memory = reconnected;
                hostMode = new(replacement);
                if (performance is not null) performance = new(replacement, device.DeviceIndex != 0xFF);
                if (advanced is not null) advanced = new(replacement, device.Name);
                DiagnosticLog.Record("workspace", "reopened verified mouse connection", new { device.Identity });
                RebaseEdits(reconnectedSnapshot, allowProfileRebase);
                return;
            }
            finally { if (!ReferenceEquals(client, replacement)) replacement.Dispose(); }
        }
        RebaseEdits(await memory.ReadAsync(), allowProfileRebase);
    }

    private void RebaseEdits(MemorySnapshot fresh, bool allowProfileRebase)
    {
        if (snapshot is null) throw new InvalidOperationException("请先读取设备。");
        if (fresh.DeviceIdentity != snapshot.DeviceIdentity || !fresh.Description.Take(11).SequenceEqual(snapshot.Description.Take(11)))
            throw new InvalidOperationException("设备或板载布局已改变，请先导出诊断包再重新选择设备。");
        // A transient macro read must not silently override a concurrent G HUB
        // profile edit. An explicit Retry applies the draft the user can see.
        if (!allowProfileRebase && snapshot.Sectors.Any(p => p.Key <= snapshot.Capabilities.ProfileCount
            && SectorChecksum.IsValid(p.Value) && (!fresh.Sectors.TryGetValue(p.Key, out var bytes) || !bytes.SequenceEqual(p.Value))))
            throw new InvalidOperationException("读取后鼠标配置被其他程序修改，编辑内容已保留；确认当前编辑内容后可点“重试板载保存”。");
        var current = fresh.GetProfiles().Single(p => p.Sector == SelectedProfile!.Sector);
        using var scope = PauseImmediateSave();
        foreach (var row in AllBindings) row.RefreshOriginal(current.GetBinding(row.Index, row.GShift));
        snapshot = fresh; refreshBeforeRetry = false;
        RecordDiagnosticContext("refreshed snapshot with edits retained");
    }
    private async Task CommitImmediateAsync()
    {
        while (IsBusy && !shuttingDown) await Task.Delay(25);
        if (!IsEditingOnboard || memory is null || snapshot is null || SelectedProfile is null) return;
        // The UI remains disabled during the short transaction, so a read-back cannot overwrite a newer gesture.
        IsBusy = true; HasError = false; OnboardSaveFailed = false;
        try { await CommitImmediateCoreAsync(); }
        finally { IsBusy = false; }
    }
    private async Task CommitImmediateCoreAsync()
    {
        if (refreshBeforeRetry)
        {
            var allowRebase = userRequestedRetry; userRequestedRetry = false;
            await RefreshSnapshotKeepingEditsAsync(allowRebase);
        }
        MemoryWritePlan plan;
        try { plan = PrepareWrite(); }
        catch (MacroStorageException error)
        {
            // Only planning failed, so no flash transaction has started. Re-read
            // once without clearing drafts; a transient/stale sector can recover.
            DiagnosticLog.Record("workspace", "macro storage recovery read", error: error);
            await RefreshSnapshotKeepingEditsAsync();
            try { plan = PrepareWrite(); }
            catch (MacroStorageException)
            {
                var backups = new List<MemorySnapshot>();
                if (Directory.Exists(BackupDirectory) && (File.GetAttributes(BackupDirectory) & FileAttributes.ReparsePoint) == 0)
                    foreach (var path in Directory.GetFiles(BackupDirectory, "*.json"))
                    {
                        try
                        {
                            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                            backups.Add(await OnboardMemory.LoadBackupAsync(path));
                        }
                        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                        { DiagnosticLog.Record("memory-recovery", "backup unavailable", new { path }, failure); }
                    }
                plan = PrepareWrite(backups);
            }
        }
        if (plan.Changes.Count == 0)
        {
            stagedLights.Remove(SelectedProfile!.Sector);
            PersistenceStatus = "已保存到板载";
            await SaveOnboardMacroLinksAsync();
            return;
        }
        await StopRuntimeCoreAsync();
        PersistenceStatus = "正在写入并校验…";
        var result = await memory!.CommitAsync(plan, BackupDirectory);
        LastBackupPath = result.BackupPath;
        snapshot = await memory.ReadAsync();
        using var scope = PauseImmediateSave();
        var currentProfile = snapshot.GetProfiles().Single(p => p.Sector == SelectedProfile!.Sector);
        foreach (var row in AllBindings) row.RefreshOriginal(currentProfile.GetBinding(row.Index, row.GShift));
        foreach (var profile in snapshot.GetProfiles())
        {
            var existing = Profiles.FirstOrDefault(p => p.Sector == profile.Sector);
            existing?.RefreshFrom(profile);
            if (SelectedProfile?.Sector == profile.Sector) SelectedProfile.RefreshFrom(profile);
        }
        stagedLights.Remove(SelectedProfile!.Sector);
        await RefreshCurrentPerformanceAsync();
        PersistenceStatus = !HasRgbLighting || CanStoreSelectedLightsyncPreset() ? "已保存到板载 · 读回一致" : "按键/性能已保存 · 当前灯效需软件";
        ModeSummary = $"板载模式 · 活动配置 {snapshot.ActiveSector}";
        StatusTitle = "板载配置已自动保存";
        Status = $"写入并校验 {result.SectorsWritten} 个扇区。";
        await SaveOnboardMacroLinksAsync();
        RefreshProfileRouteLabels();
        UpdateCapabilities(); Log(Status);
    }
}
