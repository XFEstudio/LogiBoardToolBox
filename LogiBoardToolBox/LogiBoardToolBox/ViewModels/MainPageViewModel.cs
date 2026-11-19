using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Core.Windows;
using LogiBoardToolBox.Core.Diagnostics;

namespace LogiBoardToolBox.ViewModels;

[WinRT.GeneratedBindableCustomProperty([nameof(MainPageViewModel.SelectedMacro)], [])]
public partial class MainPageViewModel : ViewModelBase, IDisposable
{
    private HidPlusPlusClient? client;
    private OnboardMemory? memory;
    private MemorySnapshot? snapshot;
    private string? snapshotDeviceIdentity;
    private bool initialized;
    private readonly string dataDirectory = Utilities.UiVerification.IsRequested ? Utilities.UiVerification.SettingsDirectory : Path.Combine(Core.Storage.RuntimeDataPaths.GetProductDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), "Onboard");
    public string BackupDirectory => Path.Combine(dataDirectory, "Backups");
    public ObservableCollection<LogitechDevice> Devices { get; } = [];
    public ObservableCollection<MouseProfile> Profiles { get; } = [];
    public ObservableCollection<DpiStageEditor> DpiStages { get; } = [];
    public ObservableCollection<ButtonBindingEditor> StandardBindings { get; } = [];
    public ObservableCollection<ButtonBindingEditor> ShiftBindings { get; } = [];
    public IEnumerable<ButtonBindingEditor> VisibleBindings => ShowGShift ? ShiftBindings : StandardBindings;
    public List<ButtonBindingEditor> AllBindings => StandardBindings.Concat(ShiftBindings).ToList();
    public ObservableCollection<MacroEditor> Macros { get; } = [];
    public ObservableCollection<LightingEditor> Lighting { get; } = [];
    public ObservableCollection<string> Activity { get; } = [];
    public IReadOnlyList<int> DpiIndices { get; } = [1, 2, 3, 4, 5];
    public ObservableCollection<EditorChoice> ReportRates { get; } = [new("125", "125 Hz"), new("250", "250 Hz"), new("500", "500 Hz"), new("1000", "1000 Hz")];
    [ObservableProperty] public partial LogitechDevice? SelectedDevice { get; set; }
    [ObservableProperty] public partial MouseProfile? SelectedProfile { get; set; }
    [ObservableProperty] public partial MacroEditor? SelectedMacro { get; set; }
    [ObservableProperty] public partial ButtonBindingEditor? MacroTarget { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool ShowGShift { get; set; }
    [ObservableProperty] public partial bool AllowExperimental { get; set; }
    [ObservableProperty] public partial string ProfileName { get; set; } = "";
    [ObservableProperty] public partial bool ProfileEnabled { get; set; }
    [ObservableProperty] public partial int DefaultDpi { get; set; } = 1;
    [ObservableProperty] public partial int ShiftDpi { get; set; } = 1;
    [ObservableProperty] public partial EditorChoice SelectedRate { get; set; }
    [ObservableProperty] public partial string PowerSaveSeconds { get; set; } = "60";
    [ObservableProperty] public partial string PowerOffSeconds { get; set; } = "300";
    [ObservableProperty] public partial string Status { get; set; } = "连接鼠标后扫描设备。选择板载配置后，有效修改会自动保存到鼠标。";
    [ObservableProperty] public partial string StatusTitle { get; set; } = "开始使用";
    [ObservableProperty] public partial bool HasError { get; set; }
    [ObservableProperty] public partial string DeviceSummary { get; set; } = "尚未读取设备";
    [ObservableProperty] public partial string ModeSummary { get; set; } = "等待连接";
    [ObservableProperty] public partial string CapabilityNote { get; set; } = "设备能力会在读取后显示。";
    [ObservableProperty] public partial string MacroPreview { get; set; } = "编译后显示实际板载字节码和空间占用。";
    [ObservableProperty] public partial string LastBackupPath { get; set; } = "";
    public bool IsIdle => !IsBusy;
    public bool HasSnapshot => snapshot is not null;
    public bool OnboardMode => snapshot?.Mode == 1;
    public string OnboardModeText => OnboardMode ? "开启" : "关闭";
    public bool CanEdit => !IsBusy && SelectedProfile is not null && snapshot?.Capabilities.CanEditProfiles == true;
    public bool CanEditPerformance => CanEdit && snapshot?.Capabilities.CanEditPerformance == true;
    public bool CanEditLighting => CanEdit && CanStoreSelectedLightsyncPreset();
    public bool HasGShift => snapshot?.Capabilities.HasGShift == true;
    public bool IsExperimental => snapshot?.Capabilities.IsExperimental == true;
    public bool CanWrite => CanEdit && (!IsExperimental || AllowExperimental);
    public bool CanChangeMode => IsIdle && HasSnapshot;
    public MainPageViewModel()
    {
        InitializeDiagnostics();
        InitializeMacroTimeline();
        InitializeLightsync();
        SelectedRate = ReportRates[^1]; Macros.Add(new()); SelectedMacro = Macros[0];
        InitializeAssignmentPalette();
        InitializeImmediateSave();
        LuaBoardTrigger = LuaBoardTriggers[0];
    }
    public async Task InitializeAsync()
    {
        if (initialized) return;
        initialized = true;
        ReadSensitivityPresets();
        ReadLuaSettings();
        ReadAnimationLibrary();
        var path = Path.Combine(dataDirectory, "macros.json");
        if (File.Exists(path)) await RunAsync(async () =>
        {
            var saved = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path), SettingsJsonContext.Default.Macros) ?? [];
            Macros.Clear();
            foreach (var item in saved) Macros.Add(new()
            {
                Name = item.Name,
                Script = item.Script,
                HoldScript = item.HoldScript,
                ReleaseScript = item.ReleaseScript,
                UseStandardDelay = item.UseStandardDelay,
                StandardDelay = item.StandardDelay,
                ShowUpDown = item.ShowUpDown,
                Color = item.Color,
                SelectedRepeat = MacroEditor.RepeatChoices.FirstOrDefault(c => c.Id == ((int)item.Repeat).ToString()) ?? MacroEditor.RepeatChoices[0]
            });
            SelectedMacro = Macros.FirstOrDefault();
        });
        await ScanAsync();
        if (SelectedDevice is not null && !HasSnapshot) await ReadAsync();
    }
    partial void OnIsBusyChanged(bool value) => UpdateCapabilities();
    partial void OnAllowExperimentalChanged(bool value) => UpdateCapabilities();
    partial void OnShowGShiftChanged(bool value)
    {
        OnPropertyChanged(nameof(VisibleBindings));
        SelectedButton = VisibleBindings.FirstOrDefault(b => b.Index == SelectedButton?.Index) ?? VisibleBindings.FirstOrDefault();
    }
    partial void OnSelectedDeviceChanged(LogitechDevice? value)
    {
        DiagnosticLog.Record("workspace", "device selected", value);
        if (snapshotDeviceIdentity == value?.Identity) return;
        using var saveScope = PauseImmediateSave();
        // Clearing the previous software profile must not recreate its draft under slot 1
        // and restore those old-model buttons when the new mouse also opens slot 1.
        refreshingProfiles = true;
        try
        {
        SetDeviceClient(null); memory = null; snapshot = null; snapshotDeviceIdentity = null;
        hostMode = null; performance = null; sensor = null;
        PerformanceSummary = "读取鼠标后显示传感器范围和当前 DPI。";
        onboardMacroLinks = [];
        advanced = null; lightingCapabilities = null; OnPropertyChanged(nameof(AdvancedState));
        HasLightforce = false; HasHeroCalibration = false; HasStartupEffect = false;
        softwareWorkspaceDraft = null; softwareWorkspaceLighting = null;
        drafts.Clear(); stagedLights.Clear();
        Configurations.Clear(); SelectedConfiguration = null;
        OnboardSaveFailed = false;
        Profiles.Clear(); SelectedProfile = null; AllowExperimental = false;
        StandardBindings.Clear(); ShiftBindings.Clear(); SelectedButton = null; ShowGShift = false; IsSideView = false;
        NotifyDeviceModel();
        DeviceSummary = "请读取此设备的板载配置"; ModeSummary = "等待读取"; UpdateCapabilities();
        }
        finally { refreshingProfiles = false; }
    }
    partial void OnSelectedProfileChanged(MouseProfile? value)
    {
        using var saveScope = PauseImmediateSave();
        StandardBindings.Clear(); ShiftBindings.Clear(); DpiStages.Clear(); Lighting.Clear();
        if (value is not null && snapshot is not null)
        {
            ProfileName = value.Name; ProfileEnabled = value.Enabled;
            DefaultDpi = value.DefaultDpiIndex + 1; ShiftDpi = value.ShiftDpiIndex + 1;
            PowerSaveSeconds = value.PowerSaveTimeout.ToString(); PowerOffSeconds = value.PowerOffTimeout.ToString();
            LoadPerformance(value);
            for (var i = 0; i < snapshot.Capabilities.AssignableButtonCount; i++)
            {
                var name = DeviceModel.ButtonName(i);
                StandardBindings.Add(new(i, false, name, value.GetBinding(i)));
                if (snapshot.Capabilities.HasGShift) ShiftBindings.Add(new(i, true, name, value.GetBinding(i, true)));
            }
            MatchStoredMacros(forSoftware: snapshot.Mode != 1);
            for (var i = 0; i < 4; i++) Lighting.Add(new(i, value.GetLighting(i)));
            LoadLightsync(value);
            RestoreDraft(value.Sector);
            UpdateCurrentDpiStage();
            MacroTarget = StandardBindings.Count > 3 ? StandardBindings[3] : StandardBindings.FirstOrDefault();
        }
        OnPropertyChanged(nameof(VisibleBindings)); OnPropertyChanged(nameof(AllBindings)); UpdateCapabilities();
        RefreshAssignmentPalette();
        RefreshProfileRouteLabels();
        SelectedButton = VisibleBindings.FirstOrDefault();
    }
    private void UpdateCapabilities()
    {
        foreach (var name in new[] { nameof(IsIdle), nameof(HasSnapshot), nameof(OnboardMode), nameof(CanEdit), nameof(CanEditPerformance), nameof(CanEditLighting), nameof(HasGShift), nameof(IsExperimental), nameof(CanWrite), nameof(CanApplyPerformance), nameof(CanChangeDevice), nameof(CanStartSoftware), nameof(CanRetryOnboardSave), nameof(CanConfigureProfilePair), nameof(CanRemoveProfilePair), nameof(CanChangeProfileEnabled) }) OnPropertyChanged(name);
        OnPropertyChanged(nameof(OnboardModeText));
        OnPropertyChanged(nameof(LightsyncStorageNote));
        OnPropertyChanged(nameof(CanChangeMode));
        OnPropertyChanged(nameof(MacroTypeDescription)); OnPropertyChanged(nameof(ShowSoftwareToggleHint)); OnPropertyChanged(nameof(ShowMacroLoopHint));
    }
    private void Log(string text)
    {
        Console.WriteLine(text); Activity.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
        DiagnosticLog.Record("activity", text);
        while (Activity.Count > 120) Activity.RemoveAt(Activity.Count - 1);
    }
    public void ShowError(Exception ex, string title = "操作未完成")
    {
        if (ex is ImmediateSaveException)
        {
            if (OnboardSaveFailed && HasError && ex.Message == Status) return;
            title = "板载保存失败";
        }
        StatusTitle = title; Status = ex.Message; HasError = true;
        Log(ex.Message);
        RecordDiagnosticContext("operation failed", ex);
    }
    public async Task RunAsync(Func<Task> operation)
    {
        if (IsBusy) return;
        IsBusy = true; HasError = false;
        try { await operation(); } catch (Exception ex) { ShowError(ex); } finally { IsBusy = false; }
    }
    [RelayCommand]
    private async Task ScanAsync()
    {
        if (!CanChangeDevice) return;
        try { await FlushImmediateSaveAsync(); }
        catch (Exception error) { ShowError(error); return; }
        await RunAsync(async () =>
    {
        StatusTitle = "扫描设备"; Status = "正在枚举罗技 HID++ 接口，请保持鼠标唤醒…";
        var devices = await Task.Run(() => LogitechDiscovery.FindAsync(message => DiagnosticLog.Record("discovery", message)));
        var old = SelectedDevice?.Identity;
        Devices.Clear(); foreach (var device in devices) Devices.Add(device);
        SelectedDevice = Devices.FirstOrDefault(d => d.Identity == old) ?? Devices.FirstOrDefault();
        StatusTitle = devices.Count == 0 ? "未发现鼠标" : "设备已连接";
        Status = devices.Count == 0 ? "请检查接收器或 USB 连接，移动鼠标后重新扫描。" : "选择设备并点击“读取板载”。";
        Log($"发现 {devices.Count} 个罗技鼠标；板载能力将在读取后显示。");
        if (SelectedDevice is not null) await ReadCoreAsync();
        });
    }
    [RelayCommand]
    private async Task ReadAsync()
    {
        try { await FlushImmediateSaveAsync(); }
        catch (IOException error) { ShowError(error); return; }
        await RunAsync(ReadCoreAsync);
    }
    public async Task SetDeviceModeAsync(bool onboard) => await RunAsync(async () =>
    {
        if (memory is null || snapshot is null) throw new InvalidOperationException("请先读取设备。");
        await StopRuntimeCoreAsync();
        var result = await memory.SetModeAsync(onboard);
        snapshot.Mode = result.Mode; snapshot.ActiveSector = result.ActiveSector;
        await RefreshCurrentPerformanceAsync();
        ModeSummary = result.Mode == 1 ? $"板载模式 · 活动配置 {result.ActiveSector}" : "主机模式 · 当前由软件控制";
        StatusTitle = "鼠标模式已切换"; Status = ModeSummary; UpdateCapabilities();
    });
    private async Task ReadCoreAsync()
    {
        using var saveScope = PauseImmediateSave();
        await StopRuntimeCoreAsync();
        var device = SelectedDevice ?? throw new InvalidOperationException("请先选择设备。");
        SetDeviceClient(null);
        var connection = LogitechDiscovery.Open(device);
        SetDeviceClient(connection);
        hostMode = new(connection);
        if (!device.HasOnboardProfiles)
        {
            memory = null; snapshot = null; snapshotDeviceIdentity = null;
            DeviceSummary = "已识别设备；未返回 0x8100 板载配置功能。";
            ModeSummary = "板载读写不可用";
            CapabilityNote = "设备保留在列表中。软件 Lua 可独立启用；板载读写须以此设备实际返回的功能为准。";
            StatusTitle = "设备已识别"; Status = CapabilityNote; NotifyDeviceModel(); UpdateCapabilities(); return;
        }
        memory = new(connection, device.Identity, device.Name); StatusTitle = "读取板载存储";
        var read = await memory.ReadAsync(new Progress<string>(message => Status = message));
        snapshot = read; snapshotDeviceIdentity = device.Identity;
        await LoadOnboardMacroLinksAsync();
        var caps = read.Capabilities;
        DeviceSummary = caps.Summary;
        ModeSummary = read.Mode == 1 ? $"板载模式 · 活动配置 {read.ActiveSector}" : "主机模式 · 当前由软件控制";
        await ReadPerformanceAsync();
        await ReadDeviceStatusAsync();
        await ReadAdvancedSettingsAsync();
        await ReadLightingCapabilitiesAsync();
        CapabilityNote = caps.IsExperimental
            ? "此鼠标使用格式 10。匹配已识别布局后，支持按键、五档双轴 DPI、双连接回报率及格式 1 宏的实验性板载写入。选择板载配置后自动保存、备份并读回校验。灯效和 G-Shift 以设备能力为准。"
            : caps.UsesAdvancedProfile ? "已适配格式 7 的双轴 DPI、回报率、按键和宏布局。抬升距离与未知字段原样保留。" +
                (SupportsOnboardLighting ? "设备报告的受支持灯效可保存到板载。" : "当前设备没有已适配的板载灯区。") + "此型号硬件宏输出仍待实机验证。"
            : caps.CanEditProfiles ? "支持板载 DPI、回报率和按键；G-Shift、灯效、宏空间以此鼠标的实际能力为准。选择板载配置后修改自动保存，逐扇区读回校验。完整 Lua、应用识别及音画同步需要主机。"
            : "此板载布局尚未适配，当前仅提供读取与原始备份。";
        var oldSector = read.Mode == 1 ? read.ActiveSector : SelectedProfile?.Sector ?? (ushort)1;
        refreshingProfiles = true; drafts.Clear();
        Profiles.Clear(); foreach (var profile in read.GetProfiles()) Profiles.Add(profile);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Sector == oldSector) ?? Profiles.FirstOrDefault();
        refreshingProfiles = false;
        RebuildConfigurations();
        StatusTitle = "读取完成";
        var incomplete = read.ReadErrors.Count - (caps.SectorCount - read.WritableSectorCount);
        Status = incomplete > 0 ? $"已读取 {read.Sectors.Count} 个扇区。另有 {incomplete} 个扇区未能读取。"
            : $"已读取 {read.Sectors.Count} 个扇区。选择板载配置后，有效修改会自动保存并读回校验。";
        Log($"{device.Name} · {caps.Summary}"); UpdateCapabilities();
    }
    public MemoryWritePlan PrepareWrite(IReadOnlyList<MemorySnapshot>? recoveryBackups = null)
    {
        if (snapshot is null || SelectedProfile is null || memory is null) throw new InvalidOperationException("请先读取板载存储。");
        var profile = snapshot.GetProfiles().Single(p => p.Sector == SelectedProfile.Sector).Copy(); profile.Name = ProfileName.Trim(); profile.Enabled = ProfileEnabled;
        if (snapshot.Capabilities.CanEditPerformance)
        {
            ApplyPerformanceToProfile(profile);
        }
        var assignments = new List<MacroAssignment>();
        foreach (var row in AllBindings)
        {
            try
            {
                profile.SetBinding(row.Index, row.GShift, row.BuildBinding());
                if (row.SelectedAction.Id != "macro") continue;
                var matches = Macros.Where(m => m.Name == row.Value.Trim()).ToArray();
                if (matches.Length != 1) throw new FormatException("宏名称不存在或重名，请检查宏库。");
                assignments.Add(new(row.Index, row.GShift, matches[0].Compile(snapshot.Capabilities)));
            }
            catch (Exception ex) { throw new FormatException($"{row}：{ex.Message}", ex); }
        }
        var activeRgb = PrepareLightsyncWrite(profile);
        if (IsEditingOnboard && CurrentProfileRoute is { } route) ProfileSwitchPlanner.ValidateDraft(route, snapshot, profile);
        RecordDiagnosticContext("prepare write");
        var plan = recoveryBackups is null ? MemoryWritePlan.Create(snapshot, profile, assignments, AllowExperimental, activeRgb)
            : MemoryWritePlan.CreateRecoveringMacroCrc(snapshot, profile, assignments, recoveryBackups, AllowExperimental, activeRgb);
        DiagnosticLog.Record("workspace", "prepared write plan", plan);
        return plan;
    }
    public async Task CommitAsync(MemoryWritePlan plan) => await RunAsync(async () =>
    {
        await StopRuntimeCoreAsync();
        if (memory is null) throw new InvalidOperationException("设备已断开，请重新读取。");
        StatusTitle = "写入板载配置";
        var result = await memory.CommitAsync(plan, BackupDirectory, new Progress<string>(message => Status = message));
        LastBackupPath = result.BackupPath; stagedLights.Remove(SelectedProfile!.Sector); await ReadCoreAsync(); StatusTitle = "板载写入完成";
        Status = $"已写入并读回验证 {result.SectorsWritten} 个扇区，已启用板载模式。原始备份：{result.BackupPath}"; Log(Status);
    });
    public async Task ExportBackupAsync(string path) => await RunAsync(async () =>
    {
        if (memory is null) throw new InvalidOperationException("请先读取设备。");
        var current = await memory.ReadAsync(new Progress<string>(message => Status = message));
        await OnboardMemory.SaveBackupAsync(current, path);
        LastBackupPath = path; StatusTitle = "备份完成"; Status = path; Log("备份已导出：" + path);
    });
    public async Task<MemoryWritePlan> PrepareRestoreAsync(string path)
    {
        if (memory is null || snapshot is null) throw new InvalidOperationException("请先读取设备。");
        var backup = await OnboardMemory.LoadBackupAsync(path);
        var current = await memory.ReadAsync(new Progress<string>(message => Status = message));
        return MemoryWritePlan.Restore(current, backup, AllowExperimental);
    }
    [RelayCommand]
    private void AddMacro()
    {
        var index = 1; while (Macros.Any(m => m.Name == $"新宏 {index}")) index++;
        var macro = new MacroEditor { Name = $"新宏 {index}", Script = "tap A\ndelay 50\ntap B" };
        Macros.Add(macro); SelectedMacro = macro;
        RefreshAssignmentPalette();
    }
    [RelayCommand]
    private void RemoveMacro()
    {
        if (SelectedMacro is not null) Macros.Remove(SelectedMacro); SelectedMacro = Macros.FirstOrDefault();
        RefreshAssignmentPalette();
    }
    internal async Task<bool> DeleteMacroFromLibraryAsync(MacroEditor macro)
    {
        var deleted = false;
        await RunAsync(async () =>
        {
            if (SelectedMacro != macro || !Macros.Contains(macro)) return;
            var name = macro.Name;
            var current = AllBindings;
            var restartSoftware = HostMacrosRunning && current.Any(IsAssignedMacro);
            await WriteMacroLibraryAsync(macro);

            using (PauseImmediateSave())
            {
                if (Macros.Count(m => m.Name == name) == 1)
                {
                    var rows = current.Concat(drafts.Values.SelectMany(draft => draft.Normal.Concat(draft.Shifted)));
                    if (softwareWorkspaceDraft is { } software) rows = rows.Concat(software.Normal).Concat(software.Shifted);
                    foreach (var binding in rows.Distinct().Where(IsAssignedMacro))
                    {
                        binding.SelectedAction = ButtonBindingEditor.ActionChoices[0];
                        binding.Value = "";
                    }
                }
                RemoveMacro();
            }
            deleted = true;
            StatusTitle = "宏已删除";
            Status = name;
            if (restartSoftware)
            {
                await StopHostMacrosCoreAsync();
                if (current.Any(row => row.SelectedAction.Id == "macro")) await StartHostMacrosCoreAsync();
            }

            bool IsAssignedMacro(ButtonBindingEditor row) => row.SelectedAction.Id == "macro" && row.Value.Trim() == name;
        });
        return deleted;
    }
    [RelayCommand]
    private void CompileMacro()
    {
        try
        {
            var macro = SelectedMacro ?? throw new InvalidOperationException("请先选择宏。");
            var program = macro.CompileProgram();
            try
            {
                var compiled = program.ToOnboard(snapshot?.Capabilities);
                MacroPreview = $"软件执行：{program.InstructionCount} 条步骤，通过检查。\n板载编译：{compiled.Length} 字节\n{Convert.ToHexString(compiled.Bytes)}\n长宏自动分配跳转链。";
            }
            catch (NotSupportedException ex) { MacroPreview = $"软件执行：{program.InstructionCount} 条步骤，通过检查。\n板载限制：{ex.Message}"; }
        }
        catch (Exception ex) { MacroPreview = ex.Message; ShowError(ex); }
    }
    [RelayCommand]
    private void BindMacro()
    {
        try
        {
            var target = MacroTarget ?? throw new InvalidOperationException("请先选择目标按键。");
            EnsureProfileRouteEditable(target);
            var macro = SelectedMacro ?? throw new InvalidOperationException("请先选择宏。");
            _ = macro.CompileProgram();
            if (IsEditingOnboard) _ = macro.Compile(snapshot?.Capabilities);
            if (string.IsNullOrWhiteSpace(macro.Name)) throw new FormatException("宏名称不能为空。");
            target.SelectedAction = ButtonBindingEditor.ActionChoices.Single(c => c.Id == "macro"); target.Value = macro.Name;
            StatusTitle = "宏已分配"; Status = $"{macro.Name} → {target}。可启动软件宏，或预览板载写入兼容性。";
        }
        catch (Exception ex) { ShowError(ex); }
    }
    [RelayCommand]
    private async Task SaveMacrosAsync() => await RunAsync(async () =>
    {
        if (Macros.Any(m => string.IsNullOrWhiteSpace(m.Name)) || Macros.GroupBy(m => m.Name).Any(g => g.Count() > 1)) throw new FormatException("宏名称不能为空或重名。");
        foreach (var macro in Macros)
        {
            _ = macro.CompileProgram();
        }
        // Validate before persisting the library. A failed board conversion must
        // not save Toggle in the library while leaving a WhileHeld macro on the mouse.
        if (IsEditingOnboard)
            foreach (var row in AllBindings.Where(r => r.SelectedAction.Id == "macro"))
                _ = GetUniqueMacroForBinding(row.Value).Compile(snapshot?.Capabilities);
        var restartSoftware = IsEditingSoftware && HostMacrosRunning;
        if (restartSoftware) await StopHostMacrosCoreAsync();
        var path = await WriteMacroLibraryAsync();
        StatusTitle = "宏库已保存"; Status = path;
        RefreshAssignmentPalette();
        RequestImmediateSave();
        if (restartSoftware) await StartHostMacrosCoreAsync();
    });
    private async Task<string> WriteMacroLibraryAsync(MacroEditor? excluded = null)
    {
        var saved = Macros.Where(m => m != excluded).Select(m => new SavedMacro(m.Name, m.Script, m.Repeat, m.HoldScript,
            m.ReleaseScript, m.UseStandardDelay, m.StandardDelay, m.ShowUpDown, m.Color)).ToList();
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "macros.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(saved, SettingsJsonContext.Default.Macros));
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
        return path;
    }

    private sealed record SavedMacro(string Name, string Script, MacroRepeat Repeat, string HoldScript = "", string ReleaseScript = "", bool UseStandardDelay = false, string StandardDelay = "50", bool ShowUpDown = true, string Color = "#00A5DB");
    public void Dispose() { _ = StopLuaCoreAsync(); SetDeviceClient(null); }
}
