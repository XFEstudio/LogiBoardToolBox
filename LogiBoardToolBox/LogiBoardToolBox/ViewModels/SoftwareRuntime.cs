using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Windows;
using LogiBoardToolBox.Services;

namespace LogiBoardToolBox.ViewModels;

public partial class MainPageViewModel
{
    private HostMacroSession? hostMacros;
    private LightsyncSession? lightsSession;
    private HostModeLease? hostMode;
    private Microsoft.UI.Dispatching.DispatcherQueue? ui;
    [ObservableProperty] public partial bool HostMacrosRunning { get; set; }
    [ObservableProperty] public partial bool LightsyncRunning { get; set; }
    [ObservableProperty] public partial string SoftwareMacroStatus { get; set; } = "软件宏未启动";
    [ObservableProperty] public partial string MacroExecutionTrace { get; set; } = "可模拟按住 300 毫秒，查看点击 / 按住 / 松开的实际事件顺序；模拟不会发送系统输入。";
    public bool CanChangeDevice => IsIdle && !HostMacrosRunning && !LightsyncRunning && !LuaRunning;
    public bool CanStartSoftware => CanEdit && IsEditingSoftware && !HostMacrosRunning;
    partial void OnHostMacrosRunningChanged(bool value) => UpdateCapabilities();
    partial void OnLightsyncRunningChanged(bool value) => UpdateCapabilities();
    public void AttachDispatcher(Microsoft.UI.Dispatching.DispatcherQueue dispatcher) => ui = dispatcher;
    private void SessionFaulted(object? sender, Exception error) => ui?.TryEnqueue(async () =>
    {
        try { await StopRuntimeCoreAsync(); } catch (Exception cleanup) { Log(cleanup.Message); }
        ShowError(error);
    });
    [RelayCommand]
    private async Task StartSoftwareMacrosAsync() => await RunAsync(StartHostMacrosCoreAsync);
    private async Task StartHostMacrosCoreAsync()
    {
        if (client is null || snapshot is null) throw new InvalidOperationException("请先读取设备。");
        if (IsEditingOnboard) throw new InvalidOperationException("板载配置由鼠标执行。请切换到软件配置后启动软件宏。");
        await StopHostMacrosCoreAsync();
        await StopLuaCoreAsync();
        var bindings = new List<HostMacroBinding>();
        foreach (var row in AllBindings.Where(r => r.SelectedAction.Id == "macro"))
        {
            var macro = Macros.SingleOrDefault(m => m.Name == row.Value) ?? throw new FormatException("找不到宏：" + row.Value);
            var program = macro.CompileProgram(); WindowsInputSink.Validate(program);
            bindings.Add(new(row.Index, row.GShift, program));
        }
        if (!StandardBindings.Any(r => r.SelectedAction.Id != "macro" && r.BuildBinding().SequenceEqual(Core.Onboard.ButtonBinding.Mouse(1))))
            throw new InvalidOperationException("请至少保留一个普通层鼠标左键。");
        var shiftButton = StandardBindings.FirstOrDefault(r => { var b = r.BuildBinding(); return b[0] == 0x90 && b[1] == 11; })?.Index ?? -1;
        hostMode ??= new(client);
        var session = new HostMacroSession(client, new WindowsInputSink(), hostMode);
        session.Faulted += SessionFaulted;
        await session.StartAsync(bindings, shiftButton); hostMacros = session; HostMacrosRunning = true;
        SoftwareMacroStatus = $"运行中 · {bindings.Count} 个宏绑定 · 点击、按住与松开由鼠标真实按键触发";
        StatusTitle = "软件宏已启动"; Status = "关闭 G HUB 的设备控制可避免争用。点击“停止软件宏”会释放按键并恢复原映射。";
        await SaveSoftwareSettingsCoreAsync();
    }
    [RelayCommand]
    private async Task UseSoftwareMacroConfigurationAsync()
    {
        var macro = SelectedMacro;
        var targets = AllBindings.Where(b => b.SelectedAction.Id == "macro" && b.Value == macro?.Name)
            .Select(b => (b.Index, b.GShift)).ToArray();
        var software = Configurations.FirstOrDefault(c => !c.Onboard);
        if (macro is null || software is null) return;
        await SelectConfigurationAsync(software);
        if (!IsEditingSoftware || HasError) return;
        SelectedMacro = macro;
        foreach (var (index, gshift) in targets)
        {
            MacroTarget = AllBindings.Single(b => b.Index == index && b.GShift == gshift);
            BindMacroCommand.Execute(null);
        }
        StatusTitle = "已切换到软件配置";
        Status = targets.Length == 0 ? "请在“板载与执行”中选择目标按键，点击“绑定到按键”，然后启动软件宏；退出软件会停止。"
            : "触发宏按一次开始、再按一次停止。点击“板载与执行 → 启动软件宏”后生效；退出软件会停止。";
    }
    private async Task StopHostMacrosCoreAsync()
    {
        var session = hostMacros; hostMacros = null;
        try { if (session is not null) await session.DisposeAsync(); }
        finally { HostMacrosRunning = false; SoftwareMacroStatus = "软件宏已停止，已释放宏按键。"; }
    }
    [RelayCommand] private async Task StopSoftwareMacrosAsync() => await RunAsync(StopHostMacrosCoreAsync);
    [RelayCommand]
    private async Task ApplyLightsyncAsync() => await RunAsync(async () =>
    {
        if (client is null) throw new InvalidOperationException("请先读取设备。");
        if (IsEditingOnboard) throw new InvalidOperationException("板载配置会自动写入固件支持的灯效。此灯效需要切换到软件配置。");
        var settings = BuildLightsyncSettings();
        await StopLightsyncCoreAsync(); hostMode ??= new(client);
        if (!HasRgbLighting) throw new NotSupportedException("此鼠标没有可编程 RGB 灯区。");
        var session = new LightsyncSession(client, hostMode, DeviceModel.Layout == "G502X" ? Core.Lighting.LightsyncDevice.G502XZoneMap : null); session.Faulted += SessionFaulted;
        await session.StartAsync(settings); lightsSession = session; LightsyncRunning = true;
        LightsyncStatus = $"正在控制 {LedZones.Count} 个灯区 · 20 FPS · " + (settings.RequiresScreen ? "屏幕采样已启用" : settings.RequiresAudio ? "系统输出音频响应已启用" : "软件灯效运行中");
        await SaveSoftwareSettingsCoreAsync();
    });
    private async Task StopLightsyncCoreAsync()
    {
        var session = lightsSession; lightsSession = null;
        try { if (session is not null) await session.DisposeAsync(); }
        finally { LightsyncRunning = false; LightsyncStatus = "灯效已停止，设备控制已恢复。"; }
    }
    [RelayCommand] private async Task StopLightsyncAsync() => await RunAsync(StopLightsyncCoreAsync);
    [RelayCommand] private void TriggerRipple() => lightsSession?.TriggerRipple();
    public async Task StopRuntimeCoreAsync()
    {
        Exception? error = null;
        try { await StopLuaCoreAsync(); } catch (Exception ex) { error = ex; }
        try { await StopLightsyncCoreAsync(); } catch (Exception ex) { error = ex; }
        try { await StopHostMacrosCoreAsync(); } catch (Exception ex) { error = error is null ? ex : new AggregateException(error, ex); }
        if (error is not null) throw error;
    }
    private sealed class TraceSink(Action<MacroInput> send) : IMacroInputSink { public void Send(MacroInput input) => send(input); }
    [RelayCommand]
    private async Task TestMacroAsync() => await RunAsync(async () =>
    {
        var program = (SelectedMacro ?? throw new InvalidOperationException("请先选择宏。")).CompileProgram();
        var trace = new List<string>(); var time = System.Diagnostics.Stopwatch.StartNew();
        await using var runtime = new MacroRuntime(new TraceSink(e => { lock (trace) if (trace.Count < 100) trace.Add($"{time.ElapsedMilliseconds,5} ms  {e.Kind} {e.Code}{(e.Kind is MacroInputKind.Key or MacroInputKind.MouseButton or MacroInputKind.Consumer ? e.Down ? " ↓" : " ↑" : "")}"); }));
        var play = runtime.Press("test", program); await Task.Delay(300); runtime.Release("test");
        if (program.Repeat == MacroRepeat.Toggle) _ = runtime.Press("test", program);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try { await play.WaitAsync(timeout.Token); } catch (OperationCanceledException) { await runtime.StopAllAsync(); trace.Add("模拟超过 4 秒，已停止并释放按键。"); }
        MacroExecutionTrace = string.Join('\n', trace);
    });
    [RelayCommand] private async Task SaveSoftwareSettingsAsync() => await RunAsync(SaveSoftwareSettingsCoreAsync);
    private async Task SaveSoftwareSettingsCoreAsync()
    {
        if (SelectedDevice is null || SelectedProfile is null) return;
        var saved = new SoftwareSettings(SelectedDevice.Identity, SelectedProfile.Sector,
            AllBindings.Where(b => b.SelectedAction.Id == "macro").Select(b => new SoftwareAssignment(b.Index, b.GShift, b.Value)).ToArray(), BuildLightsyncSettings());
        Directory.CreateDirectory(dataDirectory);
        var path = SoftwareSettingsPath();
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true })); File.Move(path + ".tmp", path, true);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "macros.json"), JsonSerializer.Serialize(Macros.Select(m => new SavedMacro(m.Name, m.Script, m.Repeat, m.HoldScript, m.ReleaseScript, m.UseStandardDelay, m.StandardDelay, m.ShowUpDown, m.Color)), new JsonSerializerOptions { WriteIndented = true }));
        Log("软件配置已保存；启动时不会自动接管鼠标。");
    }
    private string SoftwareSettingsPath() => Path.Combine(dataDirectory, "software-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(SelectedDevice!.Identity)))[..12] + $"-{SelectedProfile!.Sector}.json");
    [RelayCommand]
    private async Task LoadSoftwareSettingsAsync() => await RunAsync(async () =>
    {
        if (SelectedDevice is null || SelectedProfile is null) throw new InvalidOperationException("请先读取设备。");
        var path = SoftwareSettingsPath();
        if (!File.Exists(path)) throw new FileNotFoundException("此配置槽还没有保存的软件配置。");
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("软件配置文件过大。");
        var saved = JsonSerializer.Deserialize<SoftwareSettings>(await File.ReadAllTextAsync(path)) ?? throw new InvalidDataException("软件配置文件无效。");
        if (saved.DeviceIdentity != SelectedDevice.Identity || saved.Sector != SelectedProfile.Sector) throw new InvalidDataException("软件配置不属于当前鼠标和配置槽。");
        saved.Lighting.Validate();
        foreach (var binding in saved.Bindings)
        {
            var row = AllBindings.FirstOrDefault(b => b.Index == binding.Button && b.GShift == binding.GShift) ?? throw new InvalidDataException("软件配置包含无效按键。");
            if (!Macros.Any(m => m.Name == binding.Macro)) throw new InvalidDataException("宏库中缺少：" + binding.Macro);
            row.SelectedAction = ButtonBindingEditor.ActionChoices.Single(c => c.Id == "macro"); row.Value = binding.Macro; row.FriendlyName = binding.Macro;
        }
        LoadLightsyncSettings(saved.Lighting);
        StatusTitle = "软件配置已载入"; Status = "点击应用按钮后生效。";
    });
    private sealed record SoftwareAssignment(int Button, bool GShift, string Macro);
    private sealed record SoftwareSettings(string DeviceIdentity, ushort Sector, SoftwareAssignment[] Bindings, Core.Lighting.LightingSettings Lighting);
    public async Task ShutdownAsync()
    {
        Exception? error = null;
        try { await FlushImmediateSaveAsync(); }
        catch (Exception failure) { error = failure; RecordDiagnosticContext("shutdown with unsaved board draft", failure); }
        shuttingDown = true;
        // Even a failed board save must stop software toggles and release held
        // inputs before the HID connection is disposed.
        try { await StopRuntimeCoreAsync(); }
        catch (Exception failure) { error = error is null ? failure : new AggregateException(error, failure); }
        finally { Dispose(); }
        if (error is not null) throw error;
    }
}
