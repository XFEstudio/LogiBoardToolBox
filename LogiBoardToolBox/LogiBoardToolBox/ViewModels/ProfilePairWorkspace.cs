using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogiBoardToolBox.Core.Onboard;

namespace LogiBoardToolBox.ViewModels;

public partial class MainPageViewModel
{
    private Dictionary<string, ProfilePairRoute> profileRoutes = new(StringComparer.Ordinal);
    private bool profileRoutesLoaded;
    public ObservableCollection<EditorChoice> PairProfileChoices { get; } = [];
    public ObservableCollection<EditorChoice> PairButtonChoices { get; } = [];
    [ObservableProperty] public partial EditorChoice? PairFirstProfile { get; set; }
    [ObservableProperty] public partial EditorChoice? PairSecondProfile { get; set; }
    [ObservableProperty] public partial EditorChoice? PairFirstButton { get; set; }
    [ObservableProperty] public partial EditorChoice? PairSecondButton { get; set; }
    [ObservableProperty] public partial bool PairIncludeGShift { get; set; }
    [ObservableProperty] public partial string PairStatus { get; set; } = "两个侧键分别直达两个板载槽位；仅启用所选两个槽位，其他槽位内容保留。";
    private ProfilePairRoute? CurrentProfileRoute => snapshot is null ? null : profileRoutes.GetValueOrDefault(snapshot.DeviceIdentity);
    public bool ProfilePairActive => snapshot is not null && ProfileSwitchPlanner.Matches(CurrentProfileRoute, snapshot);
    public bool CanConfigureProfilePair => IsIdle && CanWrite && IsEditingOnboard && snapshot?.Capabilities.ProfileCount >= 2;
    public bool CanRemoveProfilePair => IsIdle && CanWrite && CurrentProfileRoute is not null;
    public bool CanChangeProfileEnabled => !ProfilePairActive;
    private string ProfileRoutesPath => Path.Combine(dataDirectory, "profile-switch-links.json");

    private void RefreshProfilePairChoices()
    {
        if (!profileRoutesLoaded)
        {
            profileRoutesLoaded = true;
            try
            {
                if (File.Exists(ProfileRoutesPath) && new FileInfo(ProfileRoutesPath).Length <= 1024 * 1024)
                    profileRoutes = JsonSerializer.Deserialize<Dictionary<string, ProfilePairRoute>>(File.ReadAllText(ProfileRoutesPath)) ?? new(StringComparer.Ordinal);
            }
            catch (Exception error) { Log("直达配置记录读取失败：" + error.Message); }
        }
        var route = CurrentProfileRoute;
        PairProfileChoices.Clear(); foreach (var p in Profiles) PairProfileChoices.Add(new(p.Sector.ToString(),
            string.IsNullOrWhiteSpace(p.Name) ? $"板载配置 {p.Sector}" : $"板载配置 {p.Sector} · {p.Name}"));
        PairButtonChoices.Clear(); foreach (var row in StandardBindings.Where(b => b.Index >= 3)) PairButtonChoices.Add(new(row.Index.ToString(), row.Label));
        PairFirstProfile = PairProfileChoices.FirstOrDefault(p => p.Id == route?.FirstProfile.ToString()) ?? PairProfileChoices.FirstOrDefault();
        PairSecondProfile = PairProfileChoices.FirstOrDefault(p => p.Id == route?.SecondProfile.ToString()) ?? PairProfileChoices.Skip(1).FirstOrDefault();
        PairFirstButton = PairButtonChoices.FirstOrDefault(p => p.Id == route?.FirstButton.ToString()) ?? PairButtonChoices.FirstOrDefault();
        PairSecondButton = PairButtonChoices.FirstOrDefault(p => p.Id == route?.SecondButton.ToString()) ?? PairButtonChoices.Skip(1).FirstOrDefault();
        PairIncludeGShift = route?.IncludeGShift ?? false;
        RefreshProfileRouteLabels();
    }

    private void RefreshProfileRouteLabels()
    {
        var route = CurrentProfileRoute;
        var active = snapshot is not null && ProfileSwitchPlanner.Matches(route, snapshot);
        foreach (var row in AllBindings)
        {
            if (row.FriendlyName.StartsWith("直达板载配置 ", StringComparison.Ordinal)) row.FriendlyName = "";
            if (!active || !IsEditingOnboard || route is null || row.GShift && !route.IncludeGShift ||
                SelectedProfile?.Sector != route.FirstProfile && SelectedProfile?.Sector != route.SecondProfile) continue;
            if (row.Index == route.FirstButton) row.FriendlyName = $"直达板载配置 {route.FirstProfile}";
            if (row.Index == route.SecondButton) row.FriendlyName = $"直达板载配置 {route.SecondProfile}";
        }
        if (active && route is not null)
            PairStatus = $"已启用：{DeviceModel.ButtonName(route.FirstButton)} → 配置 {route.FirstProfile}，{DeviceModel.ButtonName(route.SecondButton)} → 配置 {route.SecondProfile}。关闭软件后有效；其他槽位内容保留。";
        else if (route is not null)
            PairStatus = "设备配置与直达记录已不同步。可重新写入直达，或解除并恢复仍匹配的原按键。";
        else if (PairStatus.StartsWith("已启用：", StringComparison.Ordinal) || PairStatus.StartsWith("设备配置与直达记录", StringComparison.Ordinal))
            PairStatus = "两个侧键分别直达两个板载槽位；仅启用所选两个槽位，其他槽位内容保留。";
        foreach (var name in new[] { nameof(ProfilePairActive), nameof(CanConfigureProfilePair), nameof(CanRemoveProfilePair), nameof(CanChangeProfileEnabled) }) OnPropertyChanged(name);
        OnPropertyChanged(nameof(SelectedButtonAction));
    }

    private bool IsProfileRouteButton(ButtonBindingEditor target) => IsEditingOnboard && ProfilePairActive && CurrentProfileRoute is { } route &&
        (SelectedProfile?.Sector == route.FirstProfile || SelectedProfile?.Sector == route.SecondProfile) && (!target.GShift || route.IncludeGShift) &&
        (target.Index == route.FirstButton || target.Index == route.SecondButton);
    private void EnsureProfileRouteEditable(ButtonBindingEditor target)
    {
        if (IsProfileRouteButton(target)) throw new InvalidOperationException("此侧键用于双配置直达，请到设备设置解除直达后再分配其他功能。");
    }

    [RelayCommand]
    private async Task ApplyProfilePairAsync()
    {
        if (!CanConfigureProfilePair) return;
        try { await FlushImmediateSaveAsync(); } catch (Exception e) { ShowError(e); return; }
        await RunAsync(async () =>
        {
            var current = snapshot ?? throw new InvalidOperationException("请先读取鼠标。");
            var setup = ProfileSwitchPlanner.Create(current,
                ushort.Parse(PairFirstProfile?.Id ?? "0"), ushort.Parse(PairSecondProfile?.Id ?? "0"),
                int.Parse(PairFirstButton?.Id ?? "-1"), int.Parse(PairSecondButton?.Id ?? "-1"), PairIncludeGShift, AllowExperimental, CurrentProfileRoute);
            PairStatus = "正在备份并写入两个配置的直达按键…";
            await CommitProfilePairPlanAsync(setup.Plan, setup.Route);
        });
    }
    [RelayCommand]
    private async Task RemoveProfilePairAsync()
    {
        if (!CanRemoveProfilePair || snapshot is null || CurrentProfileRoute is not { } route) return;
        try { await FlushImmediateSaveAsync(); } catch (Exception e) { ShowError(e); return; }
        await RunAsync(async () => await CommitProfilePairPlanAsync(ProfileSwitchPlanner.Remove(snapshot!, route, AllowExperimental), null));
    }
    private async Task CommitProfilePairPlanAsync(MemoryWritePlan plan, ProfilePairRoute? route)
    {
        if (memory is null) throw new InvalidOperationException("设备已断开，请重新读取。");
        await StopRuntimeCoreAsync();
        var committed = await memory.CommitAsync(plan, BackupDirectory);
        LastBackupPath = committed.BackupPath;
        var after = await memory.ReadAsync();
        if (route is not null && !ProfileSwitchPlanner.Matches(route, after)) throw new IOException("直达配置读回不匹配，请从自动备份恢复。");
        if (route is null) profileRoutes.Remove(after.DeviceIdentity); else profileRoutes[after.DeviceIdentity] = route;
        snapshot = after;
        using (PauseImmediateSave())
        {
            refreshingProfiles = true; drafts.Clear();
            try { Profiles.Clear(); foreach (var p in after.GetProfiles()) Profiles.Add(p); SelectedProfile = Profiles.Single(p => p.Sector == after.ActiveSector); }
            finally { refreshingProfiles = false; }
            RebuildConfigurations();
        }
        Directory.CreateDirectory(dataDirectory);
        try { await File.WriteAllTextAsync(ProfileRoutesPath, JsonSerializer.Serialize(profileRoutes, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception error) { throw new IOException($"板载已写入且读回一致，但本机恢复记录保存失败。自动备份位于 {LastBackupPath}。", error); }
        PairStatus = route is null ? "双配置直达已解除；原按键和槽位启用状态已恢复，其他后续修改保留。" : PairStatus;
        PersistenceStatus = "已保存到板载 · 读回一致";
        ModeSummary = $"板载模式 · 活动配置 {after.ActiveSector}";
        StatusTitle = route is null ? "直达已解除" : "双配置直达已写入"; Status = plan.Summary;
        RefreshProfileRouteLabels(); UpdateCapabilities(); Log(Status);
    }
}
