using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using LogiBoardToolBox.Core.Lighting;

namespace LogiBoardToolBox.ViewModels;

public partial class MainPageViewModel
{
    private readonly Dictionary<string, LightingSettings> savedAnimations = [];
    private LightingSettings? animationOriginal;
    private string? animationOriginalId;
    [ObservableProperty] public partial string AnimationName { get; set; } = "Ocean Wave";
    [ObservableProperty] public partial AnimationDirection SelectedAnimationDirection { get; set; }
    [ObservableProperty] public partial double AnimationDefaultSpeed { get; set; } = 100;
    [ObservableProperty] public partial double AnimationZoom { get; set; } = 100;
    [ObservableProperty] public partial double AnimationPlayhead { get; set; }
    [ObservableProperty] public partial bool AnimationEditing { get; set; }
    public IReadOnlyList<AnimationDirection> AnimationDirections { get; } = Enum.GetValues<AnimationDirection>();
    public IReadOnlyList<TransitionCurve> AnimationCurves { get; } = Enum.GetValues<TransitionCurve>();
    public event EventHandler? AnimationTimelineChanged;
    partial void OnSelectedAnimationDirectionChanged(AnimationDirection value) => LightsChanged();
    partial void OnAnimationZoomChanged(double value) => AnimationTimelineChanged?.Invoke(this, EventArgs.Empty);
    partial void OnAnimationDefaultSpeedChanged(double oldValue, double newValue)
    {
        if (loadingLights || oldValue <= 0 || newValue == oldValue) return;
        loadingLights = true;
        try { foreach (var frame in AnimationFrames) if (int.TryParse(frame.Duration, out var duration)) frame.Duration = Math.Max(1, (int)Math.Round(duration * newValue / oldValue)).ToString(); }
        finally { loadingLights = false; }
        ReflowAnimation();
    }
    private void ReadAnimationLibrary()
    {
        var path = Path.Combine(dataDirectory, "animations.json");
        if (!File.Exists(path)) return;
        try
        {
            var entries = JsonSerializer.Deserialize(File.ReadAllText(path), LogiBoardToolBox.Profiles.ProfileJsonContext.Default.Animations) ?? [];
            foreach (var (id, settings) in entries) { settings.Validate(); savedAnimations[id] = settings; AnimationPresets.Add(new(id, settings.Name)); }
        }
        catch (Exception error) { Log("动画库读取失败：" + error.Message); }
    }
    private void LoadSelectedAnimation()
    {
        if (savedAnimations.TryGetValue(SelectedAnimationPreset.Id, out var saved)) { LoadLightsyncSettings(saved); return; }
        var key = SelectedAnimationPreset.Id;
        if (key is not ("verticool" or "redwhiteblue" or "oceanwave")) { CreateAnimation(key); ReflowAnimation(); return; }
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Lighting", key + ".json")));
        var info = file.RootElement.GetProperty("keyframeInfo");
        var speed = info.GetProperty("speed").GetInt32(); var at = 0; var frames = new List<LightingFrame>();
        foreach (var entry in info.GetProperty("keyframeMap").EnumerateObject().OrderBy(e => int.Parse(e.Name)))
        {
            var value = entry.Value; var map = value.GetProperty("lightingSlots").GetProperty("infoMap").GetProperty("PERKEY_COSMETIC").GetProperty("perKeyMap").GetProperty("colorCodeMap");
            var colors = Enumerable.Range(1, 8).Select(i => Rgb.Parse(map.GetProperty(i.ToString()).GetProperty("hex").GetString()!)).ToArray();
            var curve = value.TryGetProperty("curveType", out var raw) && Enum.TryParse<TransitionCurve>(raw.GetString(), true, out var parsed) ? parsed : TransitionCurve.None;
            frames.Add(new(at, colors, curve != TransitionCurve.None, curve)); at += value.GetProperty("length").GetInt32() * speed;
        }
        LoadLightsyncSettings(BuildLightsyncSettings() with { Mode = LightsyncMode.Animation, Name = SelectedAnimationPreset.Label, DefaultFrameSpeed = speed, Period = at, Frames = frames.ToArray(), Direction = AnimationDirection.Cycle });
        AnimationTimelineChanged?.Invoke(this, EventArgs.Empty);
    }
    public void BeginAnimationEdit(bool create = false)
    {
        animationOriginal = BuildLightsyncSettings(); animationOriginalId = create ? null : SelectedAnimationPreset.Id;
        if (create)
        {
            SelectedLightsyncMode = LightsyncModes[2];
            LoadLightsyncSettings(BuildLightsyncSettings() with { Mode = LightsyncMode.Animation, Name = "新动画", Period = 1000, Frames = [new(0, Enumerable.Repeat(new Rgb(0, 184, 217), LedZones.Count).ToArray())] });
        }
        else LoadSelectedAnimation();
        if (!savedAnimations.ContainsKey(SelectedAnimationPreset.Id) && !create) AnimationName += " 副本";
        AnimationEditing = true; PlayLightsyncPreview = false; AnimationPlayhead = 0;
        SelectedAnimationFrame = AnimationFrames.First();
        AnimationTimelineChanged?.Invoke(this, EventArgs.Empty);
    }
    public void CancelAnimationEdit()
    {
        if (animationOriginal is not null) LoadLightsyncSettings(animationOriginal);
        if (animationOriginalId is not null) SelectedAnimationPreset = AnimationPresets.FirstOrDefault(p => p.Id == animationOriginalId) ?? AnimationPresets[0];
        AnimationEditing = false; PlayLightsyncPreview = true; animationOriginal = null;
    }
    public async Task SaveAnimationEditAsync()
    {
        if (string.IsNullOrWhiteSpace(AnimationName)) throw new FormatException("请为动画命名。");
        ReflowAnimation(); var settings = BuildLightsyncSettings();
        var id = animationOriginalId is not null && savedAnimations.ContainsKey(animationOriginalId) ? animationOriginalId : "user-" + Guid.NewGuid().ToString("N");
        var duplicate = savedAnimations.Any(p => p.Key != id && p.Value.Name == settings.Name);
        if (duplicate) throw new FormatException("已有同名动画，请更换名称。");
        savedAnimations[id] = settings;
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "animations.json");
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(savedAnimations, LogiBoardToolBox.Profiles.ProfileJsonContext.Default.Animations));
        File.Move(path + ".tmp", path, true);
        var previous = AnimationPresets.FirstOrDefault(c => c.Id == id); if (previous is not null) AnimationPresets.Remove(previous);
        var choice = new EditorChoice(id, settings.Name); AnimationPresets.Add(choice); SelectedAnimationPreset = choice;
        AnimationEditing = false; animationOriginal = null; PlayLightsyncPreview = true;
        LightsChanged();
    }
    public void DeleteSavedAnimation()
    {
        if (!savedAnimations.Remove(SelectedAnimationPreset.Id)) return;
        AnimationPresets.Remove(SelectedAnimationPreset); SelectedAnimationPreset = AnimationPresets[0]; LoadSelectedAnimation();
        Directory.CreateDirectory(dataDirectory); File.WriteAllText(Path.Combine(dataDirectory, "animations.json"), JsonSerializer.Serialize(savedAnimations, LogiBoardToolBox.Profiles.ProfileJsonContext.Default.Animations));
    }
    public void SelectAnimationFrame(LightingFrameEditor frame)
    {
        PlayLightsyncPreview = false; SelectedAnimationFrame = frame;
        SeekAnimation(int.Parse(frame.Milliseconds)); AnimationTimelineChanged?.Invoke(this, EventArgs.Empty);
    }
    public void MoveAnimationFrame(int from, int to)
    {
        if (from < 0 || from >= AnimationFrames.Count || to < 0 || to >= AnimationFrames.Count || from == to) return;
        loadingLights = true;
        try { AnimationFrames.Move(from, to); }
        finally { loadingLights = false; }
        ReflowAnimation();
    }
    public void DuplicateAnimationFrame()
    {
        if (SelectedAnimationFrame is not { } source || AnimationFrames.Count >= 256) return;
        var frame = new LightingFrameEditor(AnimationFrames.Count, source.Colors, 0) { Duration = source.Duration, Curve = source.Curve, Smooth = source.Smooth };
        AnimationFrames.Insert(AnimationFrames.IndexOf(source) + 1, frame); ReflowAnimation(); SelectAnimationFrame(frame);
    }
    public void InsertAnimationFrame()
    {
        if (AnimationFrames.Count >= 256) return;
        var frame = new LightingFrameEditor(AnimationFrames.Count, LedZones.Select(z => Rgb.Parse(z.Color)).ToArray(), 0) { Duration = ((int)AnimationDefaultSpeed).ToString() };
        var index = SelectedAnimationFrame is { } current ? AnimationFrames.IndexOf(current) + 1 : AnimationFrames.Count;
        AnimationFrames.Insert(index, frame); ReflowAnimation(); SelectAnimationFrame(frame);
    }
    public void ReflowAnimation()
    {
        if (loadingLights) return;
        var durations = AnimationFrames.Select(f => int.TryParse(f.Duration, out var d) && d is >= 1 and <= 60000 ? d : throw new FormatException("帧时长必须为 1–60000 毫秒。")).ToArray();
        var total = durations.Sum(); if (total is < 100 or > 65535) throw new FormatException("动画总时长必须为 100–65535 毫秒。");
        loadingLights = true;
        try { var at = 0; for (var i = 0; i < AnimationFrames.Count; i++) { AnimationFrames[i].Milliseconds = at.ToString(); at += durations[i]; } LightsyncPeriod = at; }
        finally { loadingLights = false; }
        LightsChanged(); AnimationTimelineChanged?.Invoke(this, EventArgs.Empty);
    }
    public void SeekAnimation(double time)
    {
        PlayLightsyncPreview = false; AnimationPlayhead = Math.Clamp(time, 0, LightsyncPeriod - 1);
        var colors = LightingRenderer.Render(BuildLightsyncSettings(), AnimationPlayhead);
        for (var i = 0; i < LedZones.Count; i++) LedZones[i].Preview(colors[i]);
    }
}
