using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LogiBoardToolBox.ViewModels;

public sealed record AssignmentItem(string Name, string Category, string Action, string Value, string Description, string AssignedTo = "", string Color = "#00A5DB", int MacroIndex = -1)
{
    [System.Text.Json.Serialization.JsonIgnore] public string Chip => Category == "macros" ? "M" : Category == "keys" ? "K" : Category == "system" ? "▶" : "⌘";
    [System.Text.Json.Serialization.JsonIgnore] public Microsoft.UI.Xaml.Media.SolidColorBrush ChipBrush { get { try { var c = Category == "macros" ? Core.Lighting.Rgb.Parse(Color) : new Core.Lighting.Rgb(91, 94, 98); return new(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B)); } catch (FormatException) { return new(Microsoft.UI.Colors.Cyan); } } }
    public override string ToString() => Name;
}

public partial class MainPageViewModel
{
    public IReadOnlyList<EditorChoice> AssignmentCategories { get; } =
        [new("commands", "命令"), new("keys", "按键"), new("actions", "操作"), new("macros", "宏"), new("system", "系统")];
    public ObservableCollection<AssignmentItem> AssignmentItems { get; } = [];
    [ObservableProperty] public partial EditorChoice? SelectedAssignmentCategory { get; set; }
    [ObservableProperty] public partial string AssignmentSearch { get; set; } = "";
    [ObservableProperty] public partial ButtonBindingEditor? SelectedButton { get; set; }
    [ObservableProperty] public partial bool IsSideView { get; set; }
    public bool IsFrontView => !IsSideView;
    partial void OnIsSideViewChanged(bool value) => OnPropertyChanged(nameof(IsFrontView));
    public string SelectedButtonTitle => SelectedButton?.Label ?? "在鼠标上选择一个按键";
    public string SelectedButtonAction => SelectedButton?.DisplayAction ?? "点击鼠标点位，再从左侧选择要分配的功能。";
    public bool IsMacroCategory => SelectedAssignmentCategory?.Id == "macros";
    public string AssignmentHint => IsMacroCategory ? "将宏拖至目标位置，为该设备分配宏功能。" : "将命令拖至目标位置，为该设备分配对应功能。";
    public string AssignmentSearchPlaceholder => "搜索" + SelectedAssignmentCategory?.Label;

    public void InitializeAssignmentPalette()
    {
        SelectedAssignmentCategory ??= AssignmentCategories[3];
        RefreshAssignmentPalette();
    }
    partial void OnSelectedAssignmentCategoryChanged(EditorChoice? value) { RefreshAssignmentPalette(); OnPropertyChanged(nameof(IsMacroCategory)); OnPropertyChanged(nameof(AssignmentHint)); OnPropertyChanged(nameof(AssignmentSearchPlaceholder)); }
    partial void OnAssignmentSearchChanged(string value) => RefreshAssignmentPalette();
    partial void OnSelectedButtonChanged(ButtonBindingEditor? value)
    {
        OnPropertyChanged(nameof(SelectedButtonTitle)); OnPropertyChanged(nameof(SelectedButtonAction));
        MacroTarget = value;
    }
    private void RefreshAssignmentPalette()
    {
        var items = BuildAssignmentCatalog().Where(a => a.Category == (SelectedAssignmentCategory?.Id ?? "commands"));
        if (!string.IsNullOrWhiteSpace(AssignmentSearch)) items = items.Where(a => a.Name.Contains(AssignmentSearch, StringComparison.OrdinalIgnoreCase) || a.Description.Contains(AssignmentSearch, StringComparison.OrdinalIgnoreCase));
        AssignmentItems.Clear(); foreach (var item in items) AssignmentItems.Add(item);
        OnPropertyChanged(nameof(AssignmentItems));
    }
    private IEnumerable<AssignmentItem> BuildAssignmentCatalog()
    {
        (string Name, string Value)[] commands = [("复制", "Ctrl+C"), ("粘贴", "Ctrl+V"), ("剪切", "Ctrl+X"), ("撤销", "Ctrl+Z"), ("重做", "Ctrl+Y"), ("全选", "Ctrl+A"), ("保存", "Ctrl+S"), ("查找", "Ctrl+F"), ("新建标签页", "Ctrl+T"), ("关闭标签页", "Ctrl+W"), ("切换窗口", "Alt+Tab"), ("显示桌面", "Win+D")];
        foreach (var item in commands) yield return new(item.Name, "commands", "key", item.Value, item.Value);
        for (var letter = 'A'; letter <= 'Z'; letter++) yield return new(letter.ToString(), "keys", "key", letter.ToString(), "字母键");
        for (var digit = 0; digit < 10; digit++) yield return new(digit.ToString(), "keys", "key", digit.ToString(), "数字键");
        for (var f = 1; f <= 24; f++) yield return new($"F{f}", "keys", "key", $"F{f}", "功能键");
        foreach (var key in new[] { "Enter", "Space", "Tab", "Escape", "Backspace", "Delete", "Insert", "Home", "End", "PageUp", "PageDown", "Up", "Down", "Left", "Right", "Ctrl", "Shift", "Alt", "Win" })
            yield return new(key, "keys", "key", key, "键盘按键");
        (string Name, string Action, string Value)[] mouse = [("左键点击", "mouse", "1"), ("右键点击", "mouse", "2"), ("中键点击", "mouse", "3"), ("后退", "mouse", "4"), ("前进", "mouse", "5"), ("向左滚动", "special", "tilt-left"), ("向右滚动", "special", "tilt-right"), ("向上滚动", "special", "scroll-up"), ("向下滚动", "special", "scroll-down"), ("DPI 增加", "special", "dpi-up"), ("DPI 减少", "special", "dpi-down"), ("DPI 循环", "special", "dpi-cycle"), ("默认 DPI", "special", "dpi-default"), ("DPI Shift", "special", "dpi-shift"), ("G-Shift", "special", "gshift"), ("板载配置文件循环", "special", "profile-cycle"), ("下一个配置", "special", "profile-next"), ("上一个配置", "special", "profile-prev"), ("电量显示", "special", "battery"), ("停止所有宏", "special", "macro-stop-all")];
        foreach (var item in mouse) yield return new(item.Name, "actions", item.Action, item.Value, "鼠标与板载功能");
        for (var i = 0; i < Macros.Count; i++)
        {
            var macro = Macros[i];
            yield return new(macro.Name, "macros", "macro", macro.Name, macro.SelectedRepeat.Label,
                string.Join(", ", VisibleBindings.Where(r => r.SelectedAction.Id == "macro" && r.Value == macro.Name)
                    .Select(r => r.Label.Split('·').Last().Trim())), macro.Color, i);
        }
        (string Name, string Code)[] system = [("播放 / 暂停", "0xCD"), ("停止播放", "0xB7"), ("下一曲", "0xB5"), ("上一曲", "0xB6"), ("音量增加", "0xE9"), ("音量减少", "0xEA"), ("静音", "0xE2")];
        foreach (var item in system) yield return new(item.Name, "system", "consumer", item.Code, "媒体控制");
    }
    [RelayCommand]
    private void AssignPaletteItem(AssignmentItem? item)
    {
        try
        {
            var target = SelectedButton ?? throw new InvalidOperationException("请先在鼠标视图中选择目标按键。");
            if (item is null) return;
            EnsureProfileRouteEditable(target);
            if (item.Action == "macro")
            {
                var macro = GetUniqueMacroForBinding(item.Value);
                if (IsEditingOnboard) _ = macro.Compile(snapshot?.Capabilities);
            }
            target.SelectedAction = ButtonBindingEditor.ActionChoices.Single(c => c.Id == item.Action);
            target.Value = item.Value; target.FriendlyName = item.Name;
            OnPropertyChanged(nameof(SelectedButtonAction));
            StatusTitle = "按键分配已更新";
            Status = IsEditingOnboard ? $"{target.Label} → {item.Name}。有效更改将自动写入并读回校验。"
                : $"{target.Label} → {item.Name}。启动软件宏后生效，退出软件时停止。";
            RefreshAssignmentPalette();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    internal MacroEditor GetUniqueMacroForBinding(string name)
    {
        var matches = Macros.Where(m => m.Name == name).Take(2).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("宏名称不存在或重名。请在宏列表中打开对应宏并确认名称。");
        return matches[0];
    }
    [RelayCommand]
    private void ResetSelectedButton()
    {
        if (SelectedButton is not { } target) return;
        try { EnsureProfileRouteEditable(target); } catch (Exception error) { ShowError(error); return; }
        (string Action, string Value)[] defaults = [("mouse", "1"), ("mouse", "2"), ("mouse", "3"), ("mouse", "4"), ("special", "dpi-shift"), ("mouse", "5"), ("special", "tilt-left"), ("special", "tilt-right"), ("special", "profile-cycle"), ("special", "dpi-up"), ("special", "dpi-down"), ("special", "scroll-down"), ("special", "scroll-up")];
        if (target.Index < defaults.Length) { var value = defaults[target.Index]; target.SelectedAction = ButtonBindingEditor.ActionChoices.Single(c => c.Id == value.Action); target.Value = value.Value; target.FriendlyName = Core.Onboard.ButtonBinding.Describe(target.BuildBinding()); }
        else { target.SelectedAction = ButtonBindingEditor.ActionChoices[0]; target.Value = ""; target.FriendlyName = ""; }
        OnPropertyChanged(nameof(SelectedButtonAction));
    }
    [RelayCommand]
    private void DisableSelectedButton()
    {
        if (SelectedButton is not { } target) return;
        try { EnsureProfileRouteEditable(target); } catch (Exception error) { ShowError(error); return; }
        target.SelectedAction = ButtonBindingEditor.ActionChoices.Single(c => c.Id == "disabled"); target.FriendlyName = "禁用";
        OnPropertyChanged(nameof(SelectedButtonAction));
    }
}
