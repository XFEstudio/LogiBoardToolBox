using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogiBoardToolBox.Core.Lighting;
using LogiBoardToolBox.Core.Onboard;
using Microsoft.UI.Xaml.Media;
using LogiBoardToolBox.Services;

namespace LogiBoardToolBox.ViewModels;

public partial class LedZoneEditor(int index) : ObservableObject
{
    public int Index { get; } = index;
    public string Label => $"灯区 {Index + 1}";
    [ObservableProperty] public partial string Color { get; set; } = Rgb.Hue(index / 8.0).ToString();
    [ObservableProperty] public partial bool Selected { get; set; } = true;
    [ObservableProperty] public partial SolidColorBrush PreviewBrush { get; set; } = new(Microsoft.UI.Colors.Cyan);
    public void Preview(Rgb c) => PreviewBrush = new(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B));
    partial void OnColorChanged(string value) { try { Preview(Rgb.Parse(value)); } catch (FormatException) { } }
}
public partial class LightingFrameEditor(int index, Rgb[] colors, int time) : ObservableObject
{
    public Rgb[] Colors { get; set; } = colors.ToArray();
    [ObservableProperty] public partial string Name { get; set; } = $"关键帧 {index + 1}";
    [ObservableProperty] public partial string Milliseconds { get; set; } = time.ToString();
    [ObservableProperty] public partial bool Smooth { get; set; } = true;
    [ObservableProperty] public partial TransitionCurve Curve { get; set; }
    [ObservableProperty] public partial string Duration { get; set; } = "500";
    public override string ToString() => Name;
}
[WinRT.GeneratedBindableCustomProperty([nameof(SampleRegionEditor.Label)], [])]
public partial class SampleRegionEditor(int index) : ObservableObject
{
    public string Label => $"灯区 {index + 1}";
    [ObservableProperty] public partial string X { get; set; } = (index * 12.5).ToString(System.Globalization.CultureInfo.InvariantCulture);
    [ObservableProperty] public partial string Y { get; set; } = "0";
    [ObservableProperty] public partial string Width { get; set; } = "12.5";
    [ObservableProperty] public partial string Height { get; set; } = "100";
    public SampleRegion Build() => new(Parse(X) / 100, Parse(Y) / 100, Parse(Width) / 100, Parse(Height) / 100);
    private static double Parse(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
}

public partial class MainPageViewModel
{
    private readonly Dictionary<ushort, LightingSettings> stagedLights = [];
    private bool loadingLights;
    public IReadOnlyList<EditorChoice> LightsyncModes { get; } = [new("Preset", "预设"), new("Freestyle", "自定义"), new("Animation", "动画")];
    private static readonly EditorChoice[] effectChoices =
    [new("Off", "关闭"), new("Fixed", "固定"), new("Cycle", "色彩循环"), new("Breathing", "呼吸"), new("Wave", "色彩波浪"), new("Starlight", "星光"), new("Ripple", "涟漪"), new("ScreenSampler", "屏幕采样器"), new("AudioVisualizer", "音频视觉效果"), new("Signature", "标志性灯效")];
    public IReadOnlyList<EditorChoice> LightsyncEffects => effectChoices;
    public ObservableCollection<EditorChoice> AnimationPresets { get; } = [new("verticool", "Verticool"), new("redwhiteblue", "Red White and Blue"), new("oceanwave", "Ocean Wave")];
    public ObservableCollection<LedZoneEditor> LedZones { get; } = new(Enumerable.Range(0, 8).Select(i => new LedZoneEditor(i)));
    public ObservableCollection<LightingFrameEditor> AnimationFrames { get; } = [];
    public ObservableCollection<SampleRegionEditor> SampleRegions { get; } = new(Enumerable.Range(0, 8).Select(i => new SampleRegionEditor(i)));
    public ObservableCollection<EditorChoice> Monitors { get; } = [];
    [ObservableProperty] public partial EditorChoice SelectedLightsyncMode { get; set; } = new("Preset", "预设");
    [ObservableProperty] public partial EditorChoice SelectedLightsyncEffect { get; set; } = effectChoices[2];
    [ObservableProperty] public partial EditorChoice SelectedAnimationPreset { get; set; } = new("oceanwave", "Ocean Wave");
    [ObservableProperty] public partial EditorChoice? SelectedMonitor { get; set; }
    [ObservableProperty] public partial LightingFrameEditor? SelectedAnimationFrame { get; set; }
    [ObservableProperty] public partial SampleRegionEditor? SelectedSampleRegion { get; set; }
    [ObservableProperty] public partial string LightsyncColor { get; set; } = "#00B8D9";
    [ObservableProperty] public partial string LightsyncBackground { get; set; } = "#08001A";
    [ObservableProperty] public partial double LightsyncBrightness { get; set; } = 100;
    [ObservableProperty] public partial double LightsyncPeriod { get; set; } = 4000;
    [ObservableProperty] public partial double AudioGain { get; set; } = 3;
    [ObservableProperty] public partial bool LightsyncReverse { get; set; }
    [ObservableProperty] public partial bool AnimationLoop { get; set; } = true;
    [ObservableProperty] public partial bool PlayLightsyncPreview { get; set; } = true;
    [ObservableProperty] public partial string LightsyncStatus { get; set; } = "预览仅显示在界面。点击“应用到鼠标”启动灯效。";
    [ObservableProperty] public partial string StagedLightingStatus { get; set; } = "板载灯效：保持当前配置";
    public bool IsFreestyle => SelectedLightsyncMode.Id == "Freestyle";
    public bool ShowLedSelectors => !IsPreset;
    public bool IsPreset => SelectedLightsyncMode.Id == "Preset";
    public bool IsNotScreenSampler => !IsScreenSampler;
    public bool UsesPrimaryColor => !IsPreset || SelectedLightsyncEffect.Id is "Fixed" or "Breathing" or "Starlight" or "Ripple" or "AudioVisualizer";
    public bool UsesSpeed => IsAnimation || IsPreset && SelectedLightsyncEffect.Id is not ("Off" or "Fixed" or "ScreenSampler" or "AudioVisualizer");
    public bool UsesDirection => IsAnimation || IsPreset && SelectedLightsyncEffect.Id is "Wave" or "Signature";
    public bool IsAnimation => SelectedLightsyncMode.Id == "Animation";
    public bool IsScreenSampler => SelectedLightsyncMode.Id == "Preset" && SelectedLightsyncEffect.Id == "ScreenSampler";
    public bool IsAudioVisualizer => SelectedLightsyncMode.Id == "Preset" && SelectedLightsyncEffect.Id == "AudioVisualizer";
    public string LightsyncStorageNote => !HasSnapshot ? "读取鼠标后显示可用的板载灯效。"
        : !SupportsOnboardLighting ? "当前设备没有已适配的板载灯区。可在软件配置中应用灯效。"
        : !CanStoreSelectedLightsyncPreset() ? "当前效果需要软件配置，已有板载灯效会保留。"
        : snapshot?.Capabilities.UsesAdvancedProfile == true ? "当前预设可保存到所选板载配置，空闲灯效保持原值。"
        : "当前预设可保存到所选板载配置，活动与空闲状态会一并更新。";
    public event EventHandler? LightsyncEdited;
    private bool SupportsOnboardLighting => HasRgbLighting && snapshot?.Capabilities is { } caps &&
        (caps.CanEditLighting || OnboardLighting.HasActiveRgbRecords(caps, lightingCapabilities));
    private bool CanStoreSelectedLightsyncPreset() => IsPreset &&
        Enum.TryParse<LightingEffect>(SelectedLightsyncEffect.Id, out var effect) &&
        CanStoreLightsyncPreset(effect, (int)LightsyncBrightness);
    private bool CanStoreLightsyncPreset(LightingEffect effect, int brightness)
    {
        if (!HasRgbLighting || snapshot?.Capabilities is not { } caps) return false;
        return caps.UsesAdvancedProfile
            ? OnboardLighting.CanStoreActiveRgbPreset(caps, lightingCapabilities, effect, brightness)
            : caps.CanEditLighting && OnboardLighting.EffectId(effect) is not null &&
                (effect != LightingEffect.Signature || DeviceModel.Layout == "G502X");
    }
    private void LightsChanged()
    {
        if (loadingLights) return;
        foreach (var name in new[] { nameof(IsFreestyle), nameof(IsAnimation), nameof(IsScreenSampler), nameof(IsNotScreenSampler), nameof(IsPreset), nameof(ShowLedSelectors), nameof(UsesPrimaryColor), nameof(UsesSpeed), nameof(UsesDirection), nameof(IsAudioVisualizer), nameof(CanEditLighting), nameof(LightsyncStorageNote) }) OnPropertyChanged(name);
        LightsyncEdited?.Invoke(this, EventArgs.Empty);
    }
    partial void OnSelectedLightsyncModeChanged(EditorChoice value) => LightsChanged();
    partial void OnSelectedLightsyncEffectChanged(EditorChoice value) => LightsChanged();
    partial void OnLightsyncColorChanged(string value) => LightsChanged();
    partial void OnLightsyncBackgroundChanged(string value) => LightsChanged();
    partial void OnLightsyncBrightnessChanged(double value) => LightsChanged();
    partial void OnLightsyncPeriodChanged(double oldValue, double newValue)
    {
        if (!loadingLights && oldValue > 0 && double.IsFinite(newValue))
            foreach (var frame in AnimationFrames)
                if (int.TryParse(frame.Milliseconds, out var at)) frame.Milliseconds = Math.Min((int)newValue - 1, (int)Math.Round(at * newValue / oldValue)).ToString();
        LightsChanged();
    }
    partial void OnLightsyncReverseChanged(bool value) => LightsChanged();
    partial void OnAudioGainChanged(double value) => LightsChanged();
    partial void OnAnimationLoopChanged(bool value) => LightsChanged();
    partial void OnSelectedMonitorChanged(EditorChoice? value) => LightsChanged();
    private void InitializeLightsync()
    {
        SelectedLightsyncMode = LightsyncModes[0];
        SelectedAnimationPreset = AnimationPresets.Single(p => p.Id == "oceanwave");
        foreach (var z in LedZones) ObserveLedZone(z);
        foreach (var region in SampleRegions) region.PropertyChanged += (_, _) => LightsChanged();
        AnimationFrames.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is not null) foreach (LightingFrameEditor frame in e.OldItems) frame.PropertyChanged -= FrameChanged;
            if (e.NewItems is not null) foreach (LightingFrameEditor frame in e.NewItems) frame.PropertyChanged += FrameChanged;
            LightsChanged();
        };
        for (var i = 0; i < ScreenSampler.GetMonitors().Count; i++) Monitors.Add(new(i.ToString(), $"显示器 {i + 1}"));
        SelectedMonitor = Monitors.FirstOrDefault();
        SelectedSampleRegion = SampleRegions[0];
        CreateAnimation("wave");
    }
    private void ObserveLedZone(LedZoneEditor z) => z.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(LedZoneEditor.Color) || loadingLights) return;
            if (SelectedAnimationFrame is not null && IsAnimation)
            { try { SelectedAnimationFrame.Colors[z.Index] = Rgb.Parse(z.Color); } catch (FormatException) { } }
            LightsChanged();
        };
    private void ConfigureLightingZones(int count)
    {
        if (count is < 1 or > 32) throw new InvalidDataException("灯区数量超出支持范围。");
        if (LedZones.Count == count) return;
        var wasLoading = loadingLights; loadingLights = true;
        try
        {
            LedZones.Clear(); SampleRegions.Clear();
            for (var i = 0; i < count; i++)
            {
                var z = new LedZoneEditor(i); ObserveLedZone(z); LedZones.Add(z);
                var region = new SampleRegionEditor(i) { X = (i * 100.0 / count).ToString(System.Globalization.CultureInfo.InvariantCulture), Width = (100.0 / count).ToString(System.Globalization.CultureInfo.InvariantCulture) };
                region.PropertyChanged += (_, _) => LightsChanged(); SampleRegions.Add(region);
            }
            SelectedSampleRegion = SampleRegions[0]; CreateAnimation("wave");
        }
        finally { loadingLights = wasLoading; }
    }
    private void FrameChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (loadingLights) return;
        if (e.PropertyName == nameof(LightingFrameEditor.Duration)) { try { ReflowAnimation(); } catch (FormatException error) { ShowError(error); } }
        else { LightsChanged(); AnimationTimelineChanged?.Invoke(this, EventArgs.Empty); }
    }
    public LightingSettings BuildLightsyncSettings()
    {
        var s = new LightingSettings
        {
            Name = AnimationName, Direction = SelectedAnimationDirection, DefaultFrameSpeed = (int)AnimationDefaultSpeed,
            Mode = Enum.Parse<LightsyncMode>(SelectedLightsyncMode.Id), Effect = Enum.Parse<LightingEffect>(SelectedLightsyncEffect.Id),
            Color = Rgb.Parse(LightsyncColor), Background = Rgb.Parse(LightsyncBackground), Brightness = (int)LightsyncBrightness,
            Period = (int)LightsyncPeriod, Reverse = LightsyncReverse, Loop = AnimationLoop, AudioGain = AudioGain,
            ZoneColors = LedZones.Select(z => Rgb.Parse(z.Color)).ToArray(),
            Frames = AnimationFrames.Select(f => new LightingFrame(int.Parse(f.Milliseconds), f.Colors.ToArray(), f.Smooth, f.Curve)).ToArray(),
            MonitorIndex = int.Parse(SelectedMonitor?.Id ?? "0"), Regions = SampleRegions.Select(r => r.Build()).ToArray()
        };
        s.Validate(); return s;
    }
    private void LoadLightsyncSettings(LightingSettings s)
    {
        s = s.ForZoneCount(LedZones.Count);
        loadingLights = true;
        try
        {
            SelectedLightsyncMode = LightsyncModes.Single(m => m.Id == s.Mode.ToString()); SelectedLightsyncEffect = LightsyncEffects.Single(m => m.Id == s.Effect.ToString());
            LightsyncColor = s.Color.ToString(); LightsyncBackground = s.Background.ToString(); LightsyncBrightness = s.Brightness; LightsyncPeriod = s.Period; LightsyncReverse = s.Reverse;
            AnimationName = s.Name; SelectedAnimationDirection = s.Direction; AnimationDefaultSpeed = s.DefaultFrameSpeed;
            AnimationLoop = s.Loop; AudioGain = s.AudioGain; SelectedMonitor = Monitors.FirstOrDefault(m => m.Id == s.MonitorIndex.ToString()) ?? Monitors.FirstOrDefault();
            for (var i = 0; i < LedZones.Count; i++)
            {
                LedZones[i].Color = s.ZoneColors[i].ToString(); var r = s.Regions[i];
                SampleRegions[i].X = (r.X * 100).ToString(System.Globalization.CultureInfo.InvariantCulture); SampleRegions[i].Y = (r.Y * 100).ToString(System.Globalization.CultureInfo.InvariantCulture);
                SampleRegions[i].Width = (r.Width * 100).ToString(System.Globalization.CultureInfo.InvariantCulture); SampleRegions[i].Height = (r.Height * 100).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            AnimationFrames.Clear(); for (var i = 0; i < s.Frames.Length; i++) AnimationFrames.Add(new(i, s.Frames[i].Colors, s.Frames[i].Milliseconds) { Smooth = s.Frames[i].Smooth, Curve = s.Frames[i].Curve, Duration = ((i + 1 < s.Frames.Length ? s.Frames[i + 1].Milliseconds : s.Period) - s.Frames[i].Milliseconds).ToString() });
            SelectedAnimationFrame = AnimationFrames.FirstOrDefault();
        }
        finally { loadingLights = false; LightsChanged(); }
    }
    private void LoadLightsync(MouseProfile profile)
    {
        loadingLights = true;
        try
        {
            if (stagedLights.TryGetValue(profile.Sector, out var s))
            {
                SelectedLightsyncMode = LightsyncModes.First(m => m.Id == s.Mode.ToString()); SelectedLightsyncEffect = LightsyncEffects.First(m => m.Id == s.Effect.ToString());
                LightsyncColor = s.Color.ToString(); LightsyncPeriod = s.Period; LightsyncBrightness = s.Brightness;
                StagedLightingStatus = "板载灯效草稿：" + SelectedLightsyncEffect.Label;
            }
            else
            {
                var raw = profile.GetLighting(0); var effect = raw[0] switch { 0 => "Off", 1 => "Fixed", 3 => "Cycle", 10 => "Breathing", 15 or 16 => "Signature", _ => "Cycle" };
                SelectedLightsyncMode = LightsyncModes[0]; SelectedLightsyncEffect = LightsyncEffects.First(e => e.Id == effect);
                if (raw[0] is 1 or 10) LightsyncColor = new Rgb(raw[1], raw[2], raw[3]).ToString();
                var period = raw[0] == 10 ? (raw[4] << 8) | raw[5] : (raw[6] << 8) | raw[7];
                LightsyncPeriod = period is >= 100 and <= 65535 ? period : 4000;
                LightsyncBrightness = raw[0] == 10 ? (raw[7] is 0 or > 100 ? 100 : raw[7]) : raw[8] is 0 or > 100 ? 100 : raw[8];
                StagedLightingStatus = "板载灯效：保持当前配置（" + SelectedLightsyncEffect.Label + "）";
            }
        }
        finally { loadingLights = false; LightsChanged(); }
    }
    [RelayCommand]
    private void PaintSelectedZones()
    {
        try { var c = Rgb.Parse(LightsyncColor); foreach (var z in LedZones.Where(z => z.Selected)) z.Color = c.ToString(); }
        catch (Exception ex) { ShowError(ex); }
    }
    [RelayCommand] private void SelectAllZones() { foreach (var z in LedZones) z.Selected = true; }
    [RelayCommand] private void ClearZoneSelection() { foreach (var z in LedZones) z.Selected = false; }
    [RelayCommand]
    private void StageLightsync()
    {
        try
        {
            if (SelectedProfile is null) throw new InvalidOperationException("请先选择板载配置。");
            var s = BuildLightsyncSettings();
            if (!s.CanStoreProfile || !CanStoreLightsyncPreset(s.Effect, s.Brightness)) throw new NotSupportedException(LightsyncStorageNote);
            stagedLights[SelectedProfile.Sector] = s;
            StagedLightingStatus = "板载灯效草稿：" + SelectedLightsyncEffect.Label;
            if (IsEditingOnboard) RequestImmediateSave();
            else { StatusTitle = "灯效已加入板载草稿"; Status = "点击“预览并写入”保存到鼠标。"; }
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private (LightsyncCapabilities Device, LightingSettings Preset)? PrepareLightsyncWrite(MouseProfile profile)
    {
        if (!stagedLights.TryGetValue(profile.Sector, out var s)) return null;
        if (!s.CanStoreProfile || !CanStoreLightsyncPreset(s.Effect, s.Brightness))
            throw new NotSupportedException("当前设备的板载灯区不支持此预设。");
        if (snapshot!.Capabilities.UsesAdvancedProfile) return (lightingCapabilities!, s);
        var mode = OnboardLighting.EffectId(s.Effect)!.Value;
        for (var slot = 0; slot < 4; slot++) profile.SetLighting(slot, OnboardLighting.Create(mode == 15 && slot >= 2 ? (byte)16 : mode, s.Color.R, s.Color.G, s.Color.B, (ushort)s.Period, (byte)s.Brightness));
        return null;
    }
    partial void OnSelectedAnimationFrameChanged(LightingFrameEditor? value)
    {
        if (value is null) return;
        var wasLoading = loadingLights; loadingLights = true;
        try { for (var i = 0; i < LedZones.Count; i++) { LedZones[i].Color = value.Colors[i].ToString(); LedZones[i].Preview(value.Colors[i].Scale(LightsyncBrightness / 100)); } }
        finally { loadingLights = wasLoading; }
        LightsChanged();
    }
    [RelayCommand] private void LoadAnimationPreset() => LoadSelectedAnimation();
    private void CreateAnimation(string kind)
    {
        AnimationFrames.Clear(); var count = kind == "pulse" ? 2 : 8;
        for (var f = 0; f < count; f++)
        {
            var colors = Enumerable.Range(0, LedZones.Count).Select(i => kind switch
            { "ocean" => Rgb.Lerp(new(0, 15, 110), new(0, 240, 220), (1 + Math.Sin((i + f) * Math.PI / 4)) / 2), "pulse" => f == 0 ? new Rgb(0, 184, 217) : default, _ => Rgb.Hue((i + f) / (double)LedZones.Count) }).ToArray();
            AnimationFrames.Add(new(f, colors, (int)(LightsyncPeriod * f / count)));
        }
        SelectedAnimationFrame = AnimationFrames.First();
    }
    [RelayCommand]
    private void AddAnimationFrame()
    {
        var occupied = AnimationFrames.Select(f => int.TryParse(f.Milliseconds, out var v) ? v : -1).ToHashSet();
        var at = Enumerable.Range(0, (int)LightsyncPeriod).FirstOrDefault(i => !occupied.Contains(i), -1);
        if (at < 0 || AnimationFrames.Count >= 256) { ShowError(new InvalidOperationException("动画帧数已达到上限。")); return; }
        var frame = new LightingFrameEditor(AnimationFrames.Count, LedZones.Select(z => Rgb.Parse(z.Color)).ToArray(), at);
        AnimationFrames.Add(frame); SelectedAnimationFrame = frame;
    }
    [RelayCommand]
    private void RemoveAnimationFrame()
    {
        if (SelectedAnimationFrame is null || AnimationFrames.Count <= 1) return;
        AnimationFrames.Remove(SelectedAnimationFrame); SelectedAnimationFrame = AnimationFrames.FirstOrDefault(); LightsChanged();
    }
    public void UpdateLightPreview(double elapsed, double ripple = double.NegativeInfinity)
    {
        if (!PlayLightsyncPreview) return;
        try
        {
            if (LightsyncRunning && lightsSession?.LatestFrame is { } live && live.Count == LedZones.Count)
            { for (var i = 0; i < LedZones.Count; i++) LedZones[i].Preview(live[i]); return; }
            var s = BuildLightsyncSettings();
            var colors = LightingRenderer.Render(s, elapsed, lastPressMs: ripple);
            for (var i = 0; i < LedZones.Count; i++) LedZones[i].Preview(colors[i]);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException) { }
    }
}
