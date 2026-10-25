using CommunityToolkit.Mvvm.ComponentModel;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;

namespace LogiBoardToolBox.ViewModels;

public sealed class EditorChoice(string id, string label)
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public override string ToString() => Label;
}

public partial class DpiStageEditor : ObservableObject
{
    public int Index { get; }
    public int Slot => Index + 1;
    public string Label => $"DPI {Index + 1}";
    [ObservableProperty] public partial bool IsAdvanced { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial string Value { get; set; } = "800";
    [ObservableProperty] public partial string ValueY { get; set; } = "800";
    [ObservableProperty] public partial bool Enabled { get; set; } = true;
    [ObservableProperty] public partial bool LinkAxes { get; set; } = true;
    public bool SeparateAxes => !LinkAxes;
    [ObservableProperty] public partial double Maximum { get; set; } = 25600;
    [ObservableProperty] public partial double Minimum { get; set; } = 100;
    [ObservableProperty] public partial bool SupportsSeparateAxes { get; set; } = true;
    public double NumericValue { get => int.TryParse(Value, out var v) ? v : 100; set { if (double.IsFinite(value)) Value = ((int)value).ToString(); } }
    partial void OnValueChanged(string value) { if (LinkAxes) ValueY = value; OnPropertyChanged(nameof(NumericValue)); }
    partial void OnLinkAxesChanged(bool value) { if (value) ValueY = Value; OnPropertyChanged(nameof(SeparateAxes)); }
    public DpiStageEditor(int index, ushort dpi, ushort? dpiY = null)
    {
        Index = index; Enabled = dpi is not (0 or 65535); Value = Enabled ? dpi.ToString() : "800";
        ValueY = dpiY is null or 0 or 65535 ? Value : dpiY.Value.ToString(); LinkAxes = ValueY == Value;
    }
}

public partial class ButtonBindingEditor : ObservableObject
{
    public static IReadOnlyList<EditorChoice> ActionChoices { get; } =
    [new("keep", "保持原样"), new("mouse", "鼠标按键"), new("key", "键盘快捷键"),
     new("consumer", "媒体键"), new("special", "鼠标功能"), new("macro", "板载宏"), new("disabled", "禁用")];
    public IReadOnlyList<EditorChoice> Actions => ActionChoices;
    public int Index { get; }
    public bool GShift { get; }
    public string Label { get; }
    public string CurrentAction { get; }
    public byte[] Original { get; }
    [ObservableProperty] public partial EditorChoice SelectedAction { get; set; } = ActionChoices[0];
    [ObservableProperty] public partial string Value { get; set; } = "";
    [ObservableProperty] public partial string FriendlyName { get; set; } = "";
    public string DisplayAction => SelectedAction.Id == "keep" ? CurrentAction : string.IsNullOrWhiteSpace(FriendlyName) ? (SelectedAction.Id == "disabled" ? "禁用" : Value) : FriendlyName;
    public string ValueHint => SelectedAction.Id switch
    {
        "mouse" => "1 左键 / 2 右键 / 3 中键 / 4 后退 / 5 前进",
        "key" => "例如 Ctrl+Shift+A、F13、RightAlt",
        "consumer" => "0xE9 音量+ / 0xEA 音量- / 0xE2 静音 / 0xCD 播放",
        "special" => "dpi-up / dpi-down / dpi-cycle / dpi-shift / gshift / profile-cycle",
        "macro" => "填写宏库中的名称，或使用宏页的绑定按钮",
        _ => "此操作无需填写参数"
    };
    partial void OnSelectedActionChanged(EditorChoice value) { OnPropertyChanged(nameof(ValueHint)); OnPropertyChanged(nameof(DisplayAction)); }
    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(DisplayAction));
    partial void OnFriendlyNameChanged(string value) => OnPropertyChanged(nameof(DisplayAction));
    public ButtonBindingEditor(int index, bool gshift, string name, byte[] original)
    {
        Index = index; GShift = gshift; Label = $"{index + 1:00}  {name}";
        Original = original; CurrentAction = ButtonBinding.Describe(original);
    }
    public byte[] BuildBinding() => SelectedAction.Id switch
    {
        "keep" => (byte[])Original.Clone(),
        "disabled" => ButtonBinding.Disabled(),
        "mouse" => ButtonBinding.Mouse(MacroCompiler.Number(Value.Trim(), 1, 16)),
        "key" => BuildKeyboard(Value.Trim()),
        "consumer" => ButtonBinding.Consumer((ushort)MacroCompiler.Number(Value.Trim(), 1, 65535)),
        "special" => BuildSpecial(Value.Trim()),
        "macro" => Original,
        _ => throw new FormatException("未知按键操作。")
    };
    private static byte[] BuildKeyboard(string text)
    {
        var (modifier, usage) = HidKeys.ParseChord(text);
        return ButtonBinding.Keyboard(modifier, usage);
    }
    private static byte[] BuildSpecial(string value)
    {
        if (value.Equals("macro-stop-all", StringComparison.OrdinalIgnoreCase)) return [0x20, 0, 0, 0];
        byte function = value.ToLowerInvariant() switch
        {
            "tilt-left" => 1,
            "tilt-right" => 2,
            "dpi-up" => 3,
            "dpi-down" => 4,
            "dpi-cycle" => 5,
            "dpi-default" => 6,
            "dpi-shift" => 7,
            "profile-next" => 8,
            "profile-prev" => 9,
            "profile-cycle" => 10,
            "gshift" => 11,
            "battery" => 12,
            "scroll-down" => 16,
            "scroll-up" => 17,
            _ => throw new FormatException("未知鼠标功能。请使用参数提示中的英文名称。")
        };
        return ButtonBinding.Special(function);
    }
    public override string ToString() => (GShift ? "G-Shift · " : "普通 · ") + Label;
}

public partial class MacroEditor : ObservableObject
{
    public static IReadOnlyList<EditorChoice> RepeatChoices { get; } =
        [new("0", "不重复"), new("1", "按住时重复"), new("2", "触发"), new("3", "序列")];
    public IReadOnlyList<EditorChoice> Repeats => RepeatChoices;
    [ObservableProperty] public partial string Name { get; set; } = "复制并粘贴";
    [ObservableProperty] public partial string Script { get; set; } = "tap Ctrl+C\ndelay 80\ntap Ctrl+V";
    [ObservableProperty] public partial EditorChoice SelectedRepeat { get; set; } = RepeatChoices[0];
    [ObservableProperty] public partial string HoldScript { get; set; } = "";
    [ObservableProperty] public partial string ReleaseScript { get; set; } = "";
    [ObservableProperty] public partial bool UseStandardDelay { get; set; }
    [ObservableProperty] public partial string StandardDelay { get; set; } = "50";
    [ObservableProperty] public partial bool ShowUpDown { get; set; } = true;
    [ObservableProperty] public partial string Color { get; set; } = "#00A5DB";
    public Microsoft.UI.Xaml.Media.SolidColorBrush ColorBrush
    {
        get { try { var c = Core.Lighting.Rgb.Parse(Color); return new(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B)); } catch (FormatException) { return new(Microsoft.UI.Colors.Cyan); } }
    }
    partial void OnColorChanged(string value) => OnPropertyChanged(nameof(ColorBrush));
    public CompiledMacro Compile()
    {
        return CompileProgram().ToOnboard();
    }
    public MacroProgram CompileProgram() => MacroCompiler.CompileProgram(Script, Repeat, HoldScript, ReleaseScript,
        UseStandardDelay ? (ushort)MacroCompiler.Number(StandardDelay, 1, 65535) : (ushort)0);
    public MacroRepeat Repeat => (MacroRepeat)int.Parse(SelectedRepeat.Id);
    public override string ToString() => Name;
}

public partial class LightingEditor : ObservableObject
{
    public static IReadOnlyList<EditorChoice> ModeChoices { get; } =
        [new("keep", "保持设备当前灯效"), new("0", "关闭"), new("1", "常亮"), new("3", "色彩循环"), new("10", "呼吸")];
    public IReadOnlyList<EditorChoice> Modes => ModeChoices;
    public string Label { get; }
    public int Slot { get; }
    public string CurrentEffect { get; }
    [ObservableProperty] public partial EditorChoice SelectedMode { get; set; } = ModeChoices[0];
    [ObservableProperty] public partial string Color { get; set; } = "#00B8D9";
    [ObservableProperty] public partial string Period { get; set; } = "3000";
    [ObservableProperty] public partial string Brightness { get; set; } = "100";
    public LightingEditor(int slot, byte[] raw)
    {
        Slot = slot;
        string[] names = ["灯区 1 · 活动", "灯区 2 · 活动", "灯区 1 · 空闲", "灯区 2 · 空闲"];
        Label = names[slot]; CurrentEffect = $"灯效 0x{raw[0]:X2} · {Convert.ToHexString(raw)}";
    }
    public byte[]? BuildEffect()
    {
        if (SelectedMode.Id == "keep") return null;
        var color = Color.Trim().TrimStart('#');
        if (color.Length != 6) throw new FormatException("颜色需要六位 RGB，例如 #00B8D9。");
        var rgb = Convert.FromHexString(color);
        return OnboardLighting.Create(byte.Parse(SelectedMode.Id), rgb[0], rgb[1], rgb[2],
            (ushort)MacroCompiler.Number(Period, 1, 65535), (byte)MacroCompiler.Number(Brightness, 1, 100));
    }
}
