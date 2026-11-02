using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogiBoardToolBox.Core.Macros;

namespace LogiBoardToolBox.ViewModels;

public partial class MacroStepEditor : ObservableObject
{
    public static IReadOnlyList<EditorChoice> StepChoices { get; } =
        [new("tap", "击键"), new("key-down", "按键按下"), new("key-up", "按键松开"), new("delay", "延迟"),
         new("mouse-down", "鼠标按下"), new("mouse-up", "鼠标松开"), new("wheel", "滚轮"), new("pan", "水平滚轮"), new("move", "鼠标移动 X Y"), new("consumer-down", "媒体按下"), new("consumer-up", "媒体松开"), new("wait-release", "等待触发键松开")];
    public IReadOnlyList<EditorChoice> Kinds => StepChoices;
    [ObservableProperty] public partial EditorChoice Kind { get; set; } = StepChoices[0];
    [ObservableProperty] public partial string Value { get; set; } = "A";
    public string ToScript() => Kind.Id switch
    {
        "key-down" => $"key {Value} down",
        "key-up" => $"key {Value} up",
        "mouse-down" => $"mouse {Value} down",
        "mouse-up" => $"mouse {Value} up",
        "consumer-down" => $"consumer {Value} down",
        "consumer-up" => $"consumer {Value} up",
        "wait-release" => "wait-release",
        _ => $"{Kind.Id} {Value}"
    };
    public static MacroStepEditor FromScript(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new FormatException("空的宏步骤。");
        var kind = parts[0];
        if (kind is "key" or "mouse" or "consumer" && parts.Length == 3) kind += "-" + parts[2];
        var choice = StepChoices.FirstOrDefault(c => c.Id == kind);
        if (choice is null) throw new NotSupportedException($"步骤 {kind} 需要在高级文本编辑器中编辑。");
        return new() { Kind = choice, Value = kind == "move" ? string.Join(' ', parts.Skip(1)) : parts.Length > 1 ? parts[1] : "" };
    }
}

public partial class MainPageViewModel
{
    private bool loadingTimeline;
    private bool applyingTimeline;
    private string selectedMacroName = "";
    public ObservableCollection<MacroStepEditor> Timeline { get; } = [];
    [ObservableProperty] public partial MacroStepEditor? SelectedStep { get; set; }
    [ObservableProperty] public partial EditorChoice SelectedMacroSegment { get; set; } = new("press", "点击");
    [ObservableProperty] public partial bool IsRecording { get; set; }
    public string RecordingStatus => IsRecording ? "● 正在录制键盘和鼠标，软件内左右键也会录入；仅“停止录制”按钮不录入。" : "录制已停止";
    partial void OnIsRecordingChanged(bool value) => OnPropertyChanged(nameof(RecordingStatus));
    public IReadOnlyList<EditorChoice> MacroSegments { get; } = [new("press", "点击"), new("hold", "按住"), new("release", "松开")];
    public bool IsSequenceMacro => SelectedMacro?.Repeat == MacroRepeat.Sequence;
    public bool ShowSoftwareToggleHint => IsEditingOnboard && SelectedMacro?.Repeat == MacroRepeat.Toggle && snapshot?.Capabilities.SupportsNativeToggle != true;
    public bool ShowNativeToggleHint => IsEditingOnboard && SelectedMacro?.Repeat == MacroRepeat.Toggle && snapshot?.Capabilities.SupportsNativeToggle == true;
    public string MacroTypeDescription => SelectedMacro?.Repeat switch
    {
        MacroRepeat.Once => "按下时执行一次。",
        MacroRepeat.WhileHeld => "按住鼠标按键时重复，松开时停止并释放宏按键。",
        MacroRepeat.Toggle => IsEditingOnboard ? snapshot?.Capabilities.SupportsNativeToggle == true
            ? "按下开始重复，再次按下停止。由鼠标固件执行，关闭软件后仍有效。"
            : "此鼠标的固件未声明板载同键触发能力；不会写成持续循环或按住循环。"
            : "按下开始重复，再次按下停止。退出软件或停止软件宏时，立即停止并释放所有宏按键。",
        MacroRepeat.Sequence => "点击片段执行一次；按住片段持续循环；松开时执行松开片段一次。空的按住片段可编译为固件等待松开序列。",
        _ => "选择宏类型"
    };
    partial void OnSelectedMacroChanged(MacroEditor? oldValue, MacroEditor? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= MacroEditorChanged;
        if (newValue is not null) newValue.PropertyChanged += MacroEditorChanged;
        selectedMacroName = newValue?.Name ?? "";
        SelectedMacroSegment = MacroSegments[0];
        LoadTimeline();
        OnPropertyChanged(nameof(IsSequenceMacro)); OnPropertyChanged(nameof(MacroTypeDescription)); OnPropertyChanged(nameof(ShowSoftwareToggleHint)); OnPropertyChanged(nameof(ShowNativeToggleHint));
    }
    private void MacroEditorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MacroEditor.SelectedRepeat))
        {
            if (!IsSequenceMacro) SelectedMacroSegment = MacroSegments[0];
            OnPropertyChanged(nameof(IsSequenceMacro)); OnPropertyChanged(nameof(MacroTypeDescription)); OnPropertyChanged(nameof(ShowSoftwareToggleHint)); OnPropertyChanged(nameof(ShowNativeToggleHint));
        }
        DiagnosticLogMacroEdit(sender as MacroEditor, e.PropertyName);
        if (e.PropertyName == nameof(MacroEditor.Name) && SelectedMacro is { } renamed)
        {
            foreach (var row in AllBindings.Where(r => r.SelectedAction.Id == "macro" && r.Value == selectedMacroName))
            { row.Value = renamed.Name; row.FriendlyName = renamed.Name; }
            selectedMacroName = renamed.Name; RefreshAssignmentPalette();
        }
        if (e.PropertyName == nameof(MacroEditor.Color)) RefreshAssignmentPalette();
        if (!applyingTimeline && e.PropertyName is nameof(MacroEditor.Script) or nameof(MacroEditor.HoldScript) or nameof(MacroEditor.ReleaseScript)) LoadTimeline();
    }
    private static void DiagnosticLogMacroEdit(MacroEditor? macro, string? property)
    {
        if (macro is null || property is not (nameof(MacroEditor.SelectedRepeat) or nameof(MacroEditor.Script) or nameof(MacroEditor.HoldScript) or nameof(MacroEditor.ReleaseScript) or nameof(MacroEditor.UseStandardDelay) or nameof(MacroEditor.StandardDelay))) return;
        Core.Diagnostics.DiagnosticLog.Record("macro-editor", property, new { macro.Name, macro.Repeat, macro.Script, macro.HoldScript, macro.ReleaseScript, macro.UseStandardDelay, macro.StandardDelay });
    }
    public void InitializeMacroTimeline()
    {
        Timeline.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is not null) foreach (MacroStepEditor row in e.OldItems) row.PropertyChanged -= TimelineStepChanged;
            if (e.NewItems is not null) foreach (MacroStepEditor row in e.NewItems) row.PropertyChanged += TimelineStepChanged;
            if (!loadingTimeline) ApplyTimeline();
        };
    }
    private void TimelineStepChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (!loadingTimeline) ApplyTimeline(); }
    partial void OnSelectedMacroSegmentChanged(EditorChoice value) => LoadTimeline();
    public void LoadTimeline()
    {
        if (Timeline is null) return;
        loadingTimeline = true;
        foreach (var row in Timeline) row.PropertyChanged -= TimelineStepChanged;
        Timeline.Clear();
        try
        {
            var script = SelectedMacroSegment?.Id switch { "hold" => SelectedMacro?.HoldScript, "release" => SelectedMacro?.ReleaseScript, _ => SelectedMacro?.Script };
            foreach (var line in (script ?? "").Replace("\r", "").Split('\n').Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#')))
            {
                try { Timeline.Add(MacroStepEditor.FromScript(line)); }
                catch (NotSupportedException) { Timeline.Clear(); return; }
            }
            SelectedStep = Timeline.FirstOrDefault();
        }
        finally { loadingTimeline = false; }
    }
    [RelayCommand]
    public void ApplyTimeline()
    {
        if (SelectedMacro is not { } macro) return;
        var script = string.Join('\n', Timeline.Select(s => s.ToScript()));
        applyingTimeline = true;
        try
        {
            switch (SelectedMacroSegment.Id) { case "hold": macro.HoldScript = script; break; case "release": macro.ReleaseScript = script; break; default: macro.Script = script; break; }
        }
        finally { applyingTimeline = false; }
        MacroPreview = "步骤已更新。可编译检查后绑定到鼠标。";
    }
    [RelayCommand]
    private void AddStep(string? kind)
    {
        var choice = MacroStepEditor.StepChoices.FirstOrDefault(c => c.Id == kind) ?? MacroStepEditor.StepChoices[0];
        var step = new MacroStepEditor { Kind = choice, Value = choice.Id switch { "delay" => "50", "wheel" or "pan" => "1", "move" => "10 0", "consumer-down" or "consumer-up" => "0xCD", _ => choice.Id.StartsWith("mouse") ? "1" : "A" } };
        var index = SelectedStep is null ? Timeline.Count : Timeline.IndexOf(SelectedStep) + 1;
        Timeline.Insert(index, step); SelectedStep = step;
    }
    [RelayCommand]
    private void RemoveStep()
    {
        if (SelectedStep is not null) Timeline.Remove(SelectedStep); SelectedStep = Timeline.FirstOrDefault();
    }
    [RelayCommand]
    private void MoveStep(string? direction)
    {
        if (SelectedStep is null) return;
        var oldIndex = Timeline.IndexOf(SelectedStep); var newIndex = oldIndex + (direction == "up" ? -1 : 1);
        if (newIndex >= 0 && newIndex < Timeline.Count) Timeline.Move(oldIndex, newIndex);
    }
}
