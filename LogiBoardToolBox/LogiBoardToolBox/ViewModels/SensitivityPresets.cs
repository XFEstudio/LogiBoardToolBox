using System.Collections.ObjectModel;
using System.Text.Json;

namespace LogiBoardToolBox.ViewModels;

public sealed record SensitivityStage(int X, int Y, bool Enabled, bool Linked);
public sealed record SensitivityPreset(Guid Id, string Name, SensitivityStage[] Stages, int Default, int Shift, string? Wired, string? Wireless);

public partial class MainPageViewModel
{
    public ObservableCollection<SensitivityPreset> SensitivityPresets { get; } = [];
    private Guid? sensitivityPresetId;
    private string SensitivityFile => Path.Combine(dataDirectory, "sensitivity-presets.json");
    private void ReadSensitivityPresets()
    {
        try { if (File.Exists(SensitivityFile)) foreach (var preset in JsonSerializer.Deserialize<SensitivityPreset[]>(File.ReadAllText(SensitivityFile)) ?? []) if (preset.Stages.Length == 5) SensitivityPresets.Add(preset); }
        catch (Exception ex) { Log("读取灵敏度预设：" + ex.Message); }
    }
    public void SaveSensitivityPreset(bool create)
    {
        try
        {
            if (DpiStages.Count != 5) return;
            if (create) { sensitivityPresetId = Guid.NewGuid(); SensitivityName = "新灵敏度预设 " + (SensitivityPresets.Count + 1); }
            sensitivityPresetId ??= Guid.NewGuid();
            var record = new SensitivityPreset(sensitivityPresetId.Value, SensitivityName, DpiStages.Select(s => new SensitivityStage(int.Parse(s.Value), int.Parse(s.ValueY), s.Enabled, s.LinkAxes)).ToArray(), DefaultDpi, ShiftDpi, SelectedRate?.Id, SelectedWirelessRate?.Id);
            if (SensitivityPresets.FirstOrDefault(p => p.Id == record.Id) is { } old) SensitivityPresets[SensitivityPresets.IndexOf(old)] = record;
            else SensitivityPresets.Add(record);
            PersistSensitivity();
        }
        catch (Exception ex) { ShowError(ex); }
    }
    public void UpdateSensitivityPreset() { if (sensitivityPresetId is not null) SaveSensitivityPreset(false); }
    public void LoadSensitivityPreset(SensitivityPreset preset)
    {
        if (DpiStages.Count != 5 || preset.Stages.Length != 5) return;
        sensitivityPresetId = preset.Id; SensitivityName = preset.Name;
        for (var i = 0; i < 5; i++) { var value = preset.Stages[i]; var stage = DpiStages[i]; stage.LinkAxes = false; stage.Value = SnapDpi(value.X).ToString(); stage.ValueY = SnapDpi(value.Y).ToString(); stage.Enabled = value.Enabled; stage.LinkAxes = value.Linked; }
        DefaultDpi = Math.Clamp(preset.Default, 1, 5); ShiftDpi = Math.Clamp(preset.Shift, 1, 5);
        AdvancedDpi = preset.Stages.Any(s => !s.Linked); SelectedDpiStage = DpiStages[DefaultDpi - 1];
        SelectedRate = ReportRates.FirstOrDefault(r => r.Id == preset.Wired) ?? SelectedRate;
        SelectedWirelessRate = WirelessReportRates.FirstOrDefault(r => r.Id == preset.Wireless) ?? SelectedWirelessRate;
    }
    public void DeleteSensitivityPreset()
    {
        if (SensitivityPresets.FirstOrDefault(p => p.Id == sensitivityPresetId) is { } old) { SensitivityPresets.Remove(old); sensitivityPresetId = null; PersistSensitivity(); ResetPerformance(); SensitivityName = "默认设置"; }
    }
    private void PersistSensitivity()
    {
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(SensitivityFile, JsonSerializer.Serialize(SensitivityPresets, new JsonSerializerOptions { WriteIndented = true }));
    }
}
