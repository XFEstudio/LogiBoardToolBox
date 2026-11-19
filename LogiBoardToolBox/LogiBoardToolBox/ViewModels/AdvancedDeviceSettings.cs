using CommunityToolkit.Mvvm.ComponentModel;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Windows;
using System.Buffers.Binary;
using System.Text.Json;

namespace LogiBoardToolBox.ViewModels;

public partial class MainPageViewModel
{
    private AdvancedMouseController? advanced;
    private bool loadingAdvanced;
    public List<EditorChoice> LightforceModes { get; } = [new("1", "混合模式（节能）"), new("0", "仅光学模式")];
    [ObservableProperty] public partial EditorChoice? SelectedLightforceMode { get; set; }
    [ObservableProperty] public partial bool HasLightforce { get; set; }
    [ObservableProperty] public partial bool HasHeroCalibration { get; set; }
    [ObservableProperty] public partial bool HasStartupEffect { get; set; }
    [ObservableProperty] public partial bool? StartupEffectEnabled { get; set; }
    [ObservableProperty] public partial string HeroCalibrationStatus { get; set; } = "等待读取设备";
    public AdvancedMouseState? AdvancedState => advanced?.State;
    public List<EditorChoice> HeroChoices { get; } = [new("none", "无"), new("calibrate", "校准")];
    private async Task ReadAdvancedSettingsAsync()
    {
        advanced = new(client!, SelectedDevice!.Name);
        loadingAdvanced = true;
        try
        {
            var state = await advanced.ReadAsync();
            HasLightforce = state.LightforceSupported;
            HasHeroCalibration = (state.SensorFlags & 4) != 0;
            HasStartupEffect = state.StartupConfiguration is not null;
            StartupEffectEnabled = state.StartupEnabled;
            SelectedLightforceMode = LightforceModes.Single(m => m.Id == (state.Hybrid ? "1" : "0"));
            HeroCalibrationStatus = state.CalibrationIssue.Length != 0 ? state.CalibrationIssue : state.CalibrationSupported ? "选择“校准”，使此鼠标与另一只鼠标保持相同的移动手感。" : "此固件未提供 HERO 校准。";
        }
        catch (Exception error) { HeroCalibrationStatus = error.Message; Log("高级设备设置：" + error.Message); }
        finally { loadingAdvanced = false; OnPropertyChanged(nameof(AdvancedState)); }
    }
    partial void OnSelectedLightforceModeChanged(EditorChoice? value)
    {
        if (loadingAdvanced || value is null || Utilities.UiVerification.IsRequested) return;
        _ = ChangeLightforceAsync(value);
    }
    partial void OnStartupEffectEnabledChanged(bool? value)
    {
        if (loadingAdvanced || value is null || Utilities.UiVerification.IsRequested) return;
        _ = RunAsync(async () =>
        {
            try { await advanced!.SetStartupEffectAsync(value.Value); StatusTitle = "启动效果已保存"; Status = "设备设置已读回确认。"; }
            finally { loadingAdvanced = true; StartupEffectEnabled = advanced?.State?.StartupEnabled; loadingAdvanced = false; }
        });
    }
    private async Task ChangeLightforceAsync(EditorChoice choice) => await RunAsync(async () =>
    {
        if (advanced is null) throw new InvalidOperationException("请先读取设备。");
        try
        {
            await advanced.SetLightforceAsync(choice.Id == "1");
            StatusTitle = "LIGHTFORCE 已保存"; Status = "设备设置已读回确认。";
        }
        finally
        {
            loadingAdvanced = true;
            SelectedLightforceMode = LightforceModes.Single(c => c.Id == (advanced.State?.Hybrid == true ? "1" : "0"));
            loadingAdvanced = false;
        }
    });
    public async Task RetryHeroAsync() { await ReadAdvancedSettingsAsync(); }
    public async Task BeginHeroAsync() { if (advanced is null) throw new InvalidOperationException("请先读取设备。"); await advanced.StartCalibrationAsync(); }
    public async Task ApplyHeroCorrectionAsync(short correction) { if (advanced is null) throw new InvalidOperationException("请先读取设备。"); await advanced.SetCalibrationAsync(correction); }
    public async Task CancelHeroAsync() { if (advanced?.State?.CalibrationSupported == true) await advanced.SetCalibrationAsync(short.MinValue); }
    public async Task<short> MeasureHeroAsync(RawMouseDevice source, int referenceDpi, IProgress<double> progress, CancellationToken ct)
    {
        if (IsEditingOnboard || advanced?.State?.Calibration is not { } info || client is null || SelectedDevice is null) throw new NotSupportedException(HeroCalibrationStatus);
        var targetToken = $"VID_046D&PID_{SelectedDevice.Hid.ProductId:X4}";
        if (source.Path.Contains(targetToken, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请选择另一只源鼠标，目标鼠标不能作为自己的参考。");
        var targets = RawMouseMonitor.Devices().Where(d => d.Path.Contains(targetToken, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (targets.Length != 1) throw new InvalidOperationException("未能唯一识别目标鼠标的物理输入，无法开始校准。");
        await StopRuntimeCoreAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(55));
        using var monitor = new RawMouseMonitor(); await monitor.Ready;
        long reference = 0, target = 0;
        var distance = new TaskCompletionSource<(long Reference, long Target)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<short>(TaskCreationOptions.RunContinuationsAsynchronously);
        var feature = await client.GetFeatureAsync(0x2202, timeout.Token);
        void OnPacket(object? sender, RawMousePacket packet)
        {
            if (packet.Absolute) return;
            if (packet.Device == source.Handle) reference += packet.X;
            else if (packet.Device == targets[0].Handle) target += packet.X;
            progress.Report(Math.Min(100, Math.Abs(reference) * 100d / (referenceDpi * 10d)));
            if (Math.Abs(reference) >= referenceDpi * 10d && Math.Abs(target) >= 100) distance.TrySetResult((reference, target));
        }
        void OnSensor(object? sender, HidNotification message)
        {
            if (message.FeatureIndex != feature || message.Function != 1 || message.Data.Length < 6 || message.Data[0] != 0 || message.Data[1] != 0) return;
            var correction = BinaryPrimitives.ReadInt16BigEndian(message.Data.AsSpan(2));
            if (correction == short.MinValue) completed.TrySetException(new IOException("传感器报告校准失败，请保持两只鼠标的传感器对齐后重试。"));
            else completed.TrySetResult(correction);
        }
        monitor.Packet += OnPacket; client.Notification += OnSensor;
        var success = false;
        try
        {
            await advanced.StartCalibrationAsync(ct: timeout.Token, expectedCount: (ushort)Math.Min(65535, info.DpiX * 10));
            var first = await Task.WhenAny(distance.Task, completed.Task).WaitAsync(timeout.Token);
            if (first == completed.Task) { await completed.Task; throw new IOException("移动测量完成前传感器已结束校准，请重新对齐后重试。"); }
            var sample = await distance.Task;
            if (Math.Sign(sample.Reference) != Math.Sign(sample.Target)) throw new InvalidDataException("两只鼠标的移动方向不一致，请重新对齐。");
            var correction = AdvancedMouseController.CalculateCorrection(Math.Abs(sample.Reference), Math.Abs(sample.Target), referenceDpi, info.DpiX);
            await advanced.SetCalibrationAsync(correction, ct: timeout.Token);
            var actual = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
            if (Math.Abs(actual - correction) > 2) throw new IOException("传感器完成事件与请求的校准值不一致。");
            success = true; return actual;
        }
        finally
        {
            monitor.Packet -= OnPacket; client.Notification -= OnSensor;
            if (!success) await CancelHeroAsync();
        }
    }
    public async Task SaveHeroCalibrationAsync(string name, short correction)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new FormatException("请为校准命名。");
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "hero-calibration.json");
        await File.WriteAllTextAsync(path + ".tmp", Core.Diagnostics.DiagnosticJson.Serialize(new { Name = name.Trim(), Correction = correction, Device = SelectedDevice?.Identity, CapturedAt = DateTimeOffset.Now }));
        File.Move(path + ".tmp", path, true); HeroCalibrationStatus = $"{name.Trim()} · 修正 {correction / 1024d:P2} · 已收到传感器完成事件";
    }
}
