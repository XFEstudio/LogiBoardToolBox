using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Onboard;

namespace LogiBoardToolBox.ViewModels;

public partial class MainPageViewModel
{
    private PerformanceController? performance;
    private SensorPerformance? sensor;
    public ObservableCollection<EditorChoice> WirelessReportRates { get; } = [];
    public ObservableCollection<EditorChoice> LiveReportRates { get; } = [];
    public List<EditorChoice> DpiPresets { get; } = [new("default", "默认设置"), new("fps", "第一人称视角"), new("fps-xy", "第一人称视角 XY 轴分离"), new("mmo", "MMORPG"), new("mmo-xy", "MMORPG XY 轴分离"), new("moba", "MOBA"), new("productivity", "生产力场景"), new("rts", "RTS"), new("simulation", "模拟类游戏"), new("third", "第三人称视角"), new("third-xy", "第三人称视角 XY 轴分离")];
    [ObservableProperty] public partial string SensitivityName { get; set; } = "默认设置";
    [ObservableProperty] public partial bool AdvancedDpi { get; set; }
    partial void OnAdvancedDpiChanged(bool value) { foreach (var stage in DpiStages) stage.IsAdvanced = value; }
    [ObservableProperty] public partial EditorChoice? SelectedDpiPreset { get; set; }
    [ObservableProperty] public partial EditorChoice? SelectedWirelessRate { get; set; }
    [ObservableProperty] public partial EditorChoice? SelectedLiveRate { get; set; }
    [ObservableProperty] public partial DpiStageEditor? SelectedDpiStage { get; set; }
    [ObservableProperty] public partial string PerformanceSummary { get; set; } = "读取鼠标后显示传感器范围和当前 DPI。";
    [ObservableProperty] public partial string PerformanceNote { get; set; } = "选择一个 DPI 速度，设置默认速度与 DPI Shift。";
    [ObservableProperty] public partial string DpiInputError { get; set; } = "";
    public bool HasDpiInputError => DpiInputError.Length != 0;
    partial void OnDpiInputErrorChanged(string value) => OnPropertyChanged(nameof(HasDpiInputError));
    public string? ValidateDpiInput(DpiStageEditor stage, string text, bool yAxis)
    {
        if (!ushort.TryParse(text, out var value) || value < DpiMinimum || value > DpiMaximum)
            return $"请输入 {DpiMinimum:0}–{DpiMaximum:0} 范围内的 DPI 整数。";
        var x = yAxis ? ushort.Parse(stage.Value) : value;
        var y = stage.LinkAxes ? x : yAxis ? value : ushort.Parse(stage.ValueY);
        try { sensor?.ValidateDpi(x, y); }
        catch (ArgumentException) { return "此 DPI 不在传感器支持的数值中。"; }
        catch (NotSupportedException error) { return error.Message; }
        return null;
    }
    public bool HasIndependentAxes => sensor?.IndependentAxes == true && snapshot?.Capabilities.HasIndependentAxes == true;
    public bool HasSeparateRates => snapshot?.Capabilities.HasSeparateRates == true;
    public double DpiMinimum => sensor?.Minimum ?? 100;
    public double DpiMaximum => sensor?.Maximum ?? 25600;
    private double DisplayDpiMaximum => Math.Min(DpiMaximum, Math.Max(6400, Math.Ceiling(Math.Max(sensor?.CurrentX ?? 0, SelectedProfile?.Dpis.Max() ?? 3200) * 1.1 / 1000) * 1000));
    public bool CanApplyPerformance => CanEditPerformance && performance is not null && sensor is not null;
    public int SnapDpi(int value) => sensor?.DpiX.MinBy(dpi => Math.Abs(dpi - value)) ?? Math.Clamp(value, 100, 25600);
    private async Task ReadPerformanceAsync()
    {
        performance = new(client!, SelectedDevice?.DeviceIndex != 0xFF);
        try
        {
            LoadSensorPerformance(await performance.ReadAsync());
            PerformanceNote = snapshot?.Capabilities.IsExperimental == true ? "支持五档 DPI、独立 X/Y、默认与 DPI Shift、有线与无线回报率。格式 10 的板载扩展仍需开启实验性写入；所有未知字段会保留。" : "设置五档 DPI、默认与 DPI Shift；停用速度不会删除其他设置。";
        }
        catch (Exception ex)
        {
            sensor = null; performance = null;
            PerformanceSummary = "实时性能接口不可用：" + ex.Message;
            Log(PerformanceSummary);
        }
        foreach (var name in new[] { nameof(HasIndependentAxes), nameof(HasSeparateRates), nameof(DpiMinimum), nameof(DpiMaximum), nameof(CanApplyPerformance) }) OnPropertyChanged(name);
    }

    private void LoadSensorPerformance(SensorPerformance read)
    {
        sensor = read;
        ReportRates.Clear(); foreach (var hz in read.WiredRates) ReportRates.Add(new(hz.ToString(), $"{hz} Hz"));
        WirelessReportRates.Clear(); foreach (var hz in read.WirelessRates) WirelessReportRates.Add(new(hz.ToString(), $"{hz} Hz"));
        LiveReportRates.Clear(); foreach (var hz in read.ActualRates) LiveReportRates.Add(new(hz.ToString(), $"{hz} Hz"));
        SelectedLiveRate = LiveReportRates.FirstOrDefault(r => r.Id == (SelectedDevice?.DeviceIndex != 0xFF ? read.WirelessRate : read.WiredRate).ToString());
        PerformanceSummary = $"当前 {read.CurrentX} × {read.CurrentY} DPI · {(SelectedLiveRate?.Label ?? "回报率未提供")} · 传感器 {read.Minimum}–{read.Maximum} DPI";
    }
    private void LoadPerformance(MouseProfile profile)
    {
        var caps = snapshot!.Capabilities;
        var ys = profile.GetDpisY(caps);
        for (var i = 0; i < 5; i++) DpiStages.Add(new(i, profile.Dpis[i], ys[i]) { Minimum = DpiMinimum, Maximum = DisplayDpiMaximum, SupportsSeparateAxes = HasIndependentAxes, IsAdvanced = AdvancedDpi });
        try
        {
            SelectedRate = ReportRates.FirstOrDefault(r => r.Id == profile.GetRateHz(caps, false).ToString()) ?? ReportRates.Last();
            SelectedWirelessRate = WirelessReportRates.FirstOrDefault(r => r.Id == profile.GetRateHz(caps, true).ToString());
        }
        catch (Exception ex) { Log(ex.Message); }
        SelectedDpiStage = DpiStages.FirstOrDefault(s => s.Index == profile.DefaultDpiIndex) ?? DpiStages.FirstOrDefault();
        UpdateCurrentDpiStage();
        SelectedDpiPreset = DpiPresets[0];
    }
    private async Task RefreshCurrentPerformanceAsync()
    {
        reportedDpiSector = null;
        reportedDpiIndex = null;
        if (performance is null) return;
        try
        {
            sensor = await performance.ReadAsync();
            var rate = SelectedDevice?.DeviceIndex != 0xFF ? sensor.WirelessRate : sensor.WiredRate;
            PerformanceSummary = $"当前 {sensor.CurrentX} × {sensor.CurrentY} DPI · {rate} Hz · 已读回确认";
            UpdateCurrentDpiStage();
        }
        catch (Exception error) { PerformanceSummary = "板载已保存，实时性能读取失败：" + error.Message; Log(PerformanceSummary); }
    }
    private void ApplyPerformanceToProfile(MouseProfile profile)
    {
        var xs = DpiStages.Select(s => s.Enabled ? ushort.Parse(s.Value) : (ushort)0).ToArray();
        var ys = DpiStages.Select(s => s.Enabled ? ushort.Parse(s.LinkAxes ? s.Value : s.ValueY) : (ushort)0).ToArray();
        if (sensor is not null)
            sensor.ValidateProfileDpiEdits(xs, ys, profile.Dpis, profile.GetDpisY(snapshot!.Capabilities));
        else if (xs.Any(x => x is > 25600) || ys.Any(y => y is > 25600))
            throw new FormatException("未读取传感器能力，DPI 不能超过 25600。");
        profile.Dpis = xs; profile.SetDpisY(snapshot!.Capabilities, ys);
        profile.DefaultDpiIndex = (byte)(DefaultDpi - 1); profile.ShiftDpiIndex = (byte)(ShiftDpi - 1);
        if (SelectedRate is not null) profile.SetRateHz(snapshot.Capabilities, false, int.Parse(SelectedRate.Id));
        if (snapshot.Capabilities.HasSeparateRates && SelectedWirelessRate is not null) profile.SetRateHz(snapshot.Capabilities, true, int.Parse(SelectedWirelessRate.Id));
        profile.PowerSaveTimeout = ushort.Parse(PowerSaveSeconds); profile.PowerOffTimeout = ushort.Parse(PowerOffSeconds);
    }
    [RelayCommand]
    private void SetDpiDefault() { if (SelectedDpiStage?.Enabled == true) DefaultDpi = SelectedDpiStage.Index + 1; }
    [RelayCommand]
    private void SetDpiShift() { if (SelectedDpiStage?.Enabled == true) ShiftDpi = SelectedDpiStage.Index + 1; }
    [RelayCommand]
    private void ResetPerformance()
    {
        if (SelectedProfile is not { } profile) return;
        DpiStages.Clear(); LoadPerformance(profile);
        DefaultDpi = profile.DefaultDpiIndex + 1; ShiftDpi = profile.ShiftDpiIndex + 1;
    }
    [RelayCommand]
    private void ApplyDpiPreset()
    {
        sensitivityPresetId = null;
        int[] values = SelectedDpiPreset?.Id.Replace("-xy", "") switch
        {
            "fps" => [400, 800, 1600, 2400, 3200], "moba" => [800, 1200, 1600, 2400, 3200],
            "mmo" => [800, 1600, 2400, 3200, 6400], "rts" => [800, 1600, 2400, 3200, 4800],
            "productivity" => [800, 1200, 1600, 2400, 4000], _ => [800, 1200, 1600, 2400, 3200]
        };
        var separate = HasIndependentAxes && SelectedDpiPreset?.Id.EndsWith("-xy") == true;
        AdvancedDpi = separate;
        for (var i = 0; i < DpiStages.Count; i++) { DpiStages[i].LinkAxes = !separate; DpiStages[i].Enabled = true; DpiStages[i].Value = values[i].ToString(); DpiStages[i].ValueY = (separate ? values[i] * 3 / 4 : values[i]).ToString(); }
        DefaultDpi = 3; ShiftDpi = 1; SelectedDpiStage = DpiStages.ElementAtOrDefault(2);
    }
    [RelayCommand]
    private async Task ApplyPerformanceAsync() => await RunAsync(async () =>
    {
        if (performance is null || SelectedDpiStage is not { Enabled: true } stage) throw new InvalidOperationException("先读取设备并选择启用的 DPI 速度。");
        await performance.ApplyAsync(int.Parse(stage.Value), int.Parse(stage.LinkAxes ? stage.Value : stage.ValueY), int.Parse(SelectedLiveRate?.Id ?? "0"));
        sensor = performance.State;
        UpdateCurrentDpiStage();
        PerformanceSummary = $"当前 {performance.State!.CurrentX} × {performance.State.CurrentY} DPI · {SelectedLiveRate?.Label} · 已读回确认";
        StatusTitle = "性能设置已应用"; Status = "即时设置已生效。点击“预览并写入”可保存到当前板载配置。";
    });
}
