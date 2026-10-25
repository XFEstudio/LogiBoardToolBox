namespace LogiBoardToolBox.ViewModels;

public partial class MainPageViewModel
{
    private readonly Dictionary<ushort, ProfileDraft> drafts = [];
    private bool refreshingProfiles;
    private sealed record ProfileDraft(string Name, bool Enabled, int Default, int Shift, EditorChoice Rate, EditorChoice? WirelessRate,
        string SaveTimeout, string SleepTimeout, DpiStageEditor[] Dpis, ButtonBindingEditor[] Normal, ButtonBindingEditor[] Shifted);
    private ProfileDraft CaptureCurrentDraft() => new(ProfileName, ProfileEnabled, DefaultDpi, ShiftDpi, SelectedRate, SelectedWirelessRate,
        PowerSaveSeconds, PowerOffSeconds, DpiStages.ToArray(), StandardBindings.ToArray(), ShiftBindings.ToArray());
    partial void OnSelectedProfileChanging(Core.Onboard.MouseProfile? oldValue, Core.Onboard.MouseProfile? newValue)
    {
        if (oldValue is null || refreshingProfiles || IsEditingOnboard) return;
        drafts[oldValue.Sector] = CaptureCurrentDraft();
    }
    private void RestoreDraft(ushort sector)
    {
        if (!drafts.TryGetValue(sector, out var draft)) return;
        ProfileName = draft.Name; ProfileEnabled = draft.Enabled; DefaultDpi = draft.Default; ShiftDpi = draft.Shift;
        SelectedRate = draft.Rate; SelectedWirelessRate = draft.WirelessRate; PowerSaveSeconds = draft.SaveTimeout; PowerOffSeconds = draft.SleepTimeout;
        DpiStages.Clear(); foreach (var stage in draft.Dpis) DpiStages.Add(stage);
        StandardBindings.Clear(); foreach (var row in draft.Normal) StandardBindings.Add(row);
        ShiftBindings.Clear(); foreach (var row in draft.Shifted) ShiftBindings.Add(row);
        SelectedDpiStage = DpiStages.FirstOrDefault(s => s.Index == DefaultDpi - 1);
    }
}
