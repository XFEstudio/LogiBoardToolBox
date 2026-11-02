namespace LogiBoardToolBox.Core.Onboard;

public sealed record ProfileRouteOriginal(ushort Profile, int Button, bool GShift, byte[] Binding);
public sealed record ProfilePairRoute(string DeviceIdentity, string Layout, ushort FirstProfile, ushort SecondProfile,
    int FirstButton, int SecondButton, bool IncludeGShift, Dictionary<ushort, bool> OriginalEnabled,
    List<ProfileRouteOriginal> OriginalBindings);
public sealed record ProfilePairWrite(MemoryWritePlan Plan, ProfilePairRoute Route);

/// <summary>Absolute selection between two stored slots using existing native next/previous actions.
/// The button pointing at the current slot is disabled, so pressing it again is idempotent.
/// No host key listener, invented mouse action 13, or macro storage is involved.</summary>
public static class ProfileSwitchPlanner
{
    public static bool IsForDevice(ProfilePairRoute route, MemorySnapshot snapshot) =>
        route.DeviceIdentity == snapshot.DeviceIdentity && route.Layout == Convert.ToHexString(snapshot.Description.AsSpan(0, 11));

    public static byte[] Binding(ProfilePairRoute route, ushort source, int button) =>
        (button == route.FirstButton ? route.FirstProfile : route.SecondProfile) is var target && target == source
            ? ButtonBinding.Disabled() : ButtonBinding.Special(target > source ? (byte)8 : (byte)9);

    public static bool Matches(ProfilePairRoute? route, MemorySnapshot snapshot)
    {
        if (route is null || !IsForDevice(route, snapshot)) return false;
        try
        {
            Validate(snapshot, route.FirstProfile, route.SecondProfile, route.FirstButton, route.SecondButton, route.IncludeGShift);
            var profiles = snapshot.GetProfiles();
            if (!profiles.Where(p => p.Enabled).Select(p => p.Sector).ToHashSet().SetEquals([route.FirstProfile, route.SecondProfile])) return false;
            foreach (var p in profiles.Where(p => p.Sector == route.FirstProfile || p.Sector == route.SecondProfile))
            foreach (var layer in route.IncludeGShift ? new[] { false, true } : new[] { false })
            foreach (var button in new[] { route.FirstButton, route.SecondButton })
                if (!p.GetBinding(button, layer).AsSpan().SequenceEqual(Binding(route, p.Sector, button))) return false;
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or NotSupportedException) { return false; }
    }

    public static ProfilePairWrite Create(MemorySnapshot snapshot, ushort first, ushort second, int firstButton, int secondButton,
        bool includeGShift = false, bool allowExperimental = false, ProfilePairRoute? previous = null)
    {
        Validate(snapshot, first, second, firstButton, secondButton, includeGShift);
        var working = Copy(snapshot);
        if (previous is not null && IsForDevice(previous, snapshot)) Apply(working, Remove(snapshot, previous, allowExperimental));
        var original = working.GetProfiles();
        // Reuse the normal layout guards and factory-initialization rules.
        Apply(working, MemoryWritePlan.Create(working, original.Single(p => p.Sector == first).Copy(), [], allowExperimental));
        var route = new ProfilePairRoute(snapshot.DeviceIdentity, Convert.ToHexString(snapshot.Description.AsSpan(0, 11)),
            first, second, firstButton, secondButton, includeGShift, original.ToDictionary(p => p.Sector, p => p.Enabled), []);
        foreach (var p in original.Where(p => p.Sector == first || p.Sector == second))
        {
            var changed = p.Copy();
            foreach (var layer in includeGShift ? new[] { false, true } : new[] { false })
            foreach (var button in new[] { firstButton, secondButton })
            {
                route.OriginalBindings.Add(new(p.Sector, button, layer, (byte[])p.GetBinding(button, layer).Clone()));
                changed.SetBinding(button, layer, Binding(route, p.Sector, button));
            }
            changed.Validate(snapshot.Capabilities); working.Sectors[p.Sector] = changed.ToBytes();
        }
        SetEnabled(working, original.ToDictionary(p => p.Sector, p => p.Sector == first || p.Sector == second));
        var active = snapshot.ActiveSector == first || snapshot.ActiveSector == second ? snapshot.ActiveSector : first;
        var plan = Between(snapshot, working, active, $"双配置直达：侧键 {firstButton + 1} → 板载配置 {first}，侧键 {secondButton + 1} → 板载配置 {second}。\n" +
            "仅启用这两个槽位，其他配置内容保留。当前槽位对应的直达键不切换；关闭软件后仍由鼠标执行。写入前备份，写入后读回。");
        return new(plan, route);
    }

    public static MemoryWritePlan Remove(MemorySnapshot snapshot, ProfilePairRoute route, bool allowExperimental = false)
    {
        Validate(snapshot, route.FirstProfile, route.SecondProfile, route.FirstButton, route.SecondButton, route.IncludeGShift);
        if (!IsForDevice(route, snapshot)) throw new InvalidDataException("双配置直达记录不属于当前设备布局。");
        if (snapshot.Capabilities.IsExperimental && !allowExperimental) throw new NotSupportedException("格式 10 需先允许实验性写入。");
        if (route.OriginalBindings is null || route.OriginalEnabled is null) throw new InvalidDataException("直达恢复记录不完整。");
        var working = Copy(snapshot); var profiles = working.GetProfiles();
        var expected = (from p in new[] { route.FirstProfile, route.SecondProfile }
            from layer in route.IncludeGShift ? new[] { false, true } : new[] { false }
            from button in new[] { route.FirstButton, route.SecondButton } select (p, button, layer)).ToHashSet();
        if (route.OriginalBindings.Count != expected.Count || !expected.SetEquals(route.OriginalBindings.Select(b => (b.Profile, b.Button, b.GShift))) ||
            !profiles.Select(p => p.Sector).ToHashSet().SetEquals(route.OriginalEnabled.Keys) || !route.OriginalEnabled.Values.Any(v => v))
            throw new InvalidDataException("直达恢复记录的槽位或按键不完整，请使用完整自动备份恢复。");
        _ = MemoryWritePlan.Create(snapshot, profiles[0].Copy(), [], allowExperimental);
        foreach (var p in profiles)
        {
            var changed = p.Copy();
            foreach (var old in route.OriginalBindings.Where(b => b.Profile == p.Sector))
            {
                if (old.Binding?.Length != 4 || old.Button != route.FirstButton && old.Button != route.SecondButton) throw new InvalidDataException("直达恢复记录中的按键无效。");
                // Preserve any unrelated edits made later by the user or G HUB.
                if (p.GetBinding(old.Button, old.GShift).AsSpan().SequenceEqual(Binding(route, p.Sector, old.Button)))
                    changed.SetBinding(old.Button, old.GShift, old.Binding);
            }
            if (!changed.ToBytes().AsSpan().SequenceEqual(p.ToBytes())) working.Sectors[p.Sector] = changed.ToBytes();
        }
        var onlyPair = profiles.Where(p => p.Enabled).Select(p => p.Sector).ToHashSet().SetEquals([route.FirstProfile, route.SecondProfile]);
        if (onlyPair && route.OriginalEnabled.Values.Any(v => v)) SetEnabled(working, route.OriginalEnabled);
        var enabled = working.GetProfiles().Where(p => p.Enabled).Select(p => p.Sector).ToArray();
        var active = enabled.Contains(snapshot.ActiveSector) ? snapshot.ActiveSector : enabled.First();
        return Between(snapshot, working, active, "解除双配置直达，恢复设置前的按键和槽位启用状态；后续的其他配置修改保留。");
    }

    public static void ValidateDraft(ProfilePairRoute route, MemorySnapshot snapshot, MouseProfile draft)
    {
        if (!Matches(route, snapshot)) return;
        var pair = draft.Sector == route.FirstProfile || draft.Sector == route.SecondProfile;
        if (draft.Enabled != pair) throw new InvalidOperationException("双配置直达启用期间需保持所选两个槽位启用。请先解除直达，再修改槽位启用状态。");
        if (!pair) return;
        foreach (var layer in route.IncludeGShift ? new[] { false, true } : new[] { false })
        foreach (var button in new[] { route.FirstButton, route.SecondButton })
            if (!draft.GetBinding(button, layer).AsSpan().SequenceEqual(Binding(route, draft.Sector, button)))
                throw new InvalidOperationException("此侧键用于双配置直达，请先解除直达，再分配其他功能。");
    }

    private static void Validate(MemorySnapshot snapshot, ushort first, ushort second, int a, int b, bool shift)
    {
        var caps = snapshot.Capabilities;
        if (!caps.CanEditProfiles || caps.ProfileCount < 2) throw new NotSupportedException("此鼠标没有两个可写板载槽位。");
        if (first < 1 || first > caps.ProfileCount || second < 1 || second > caps.ProfileCount || first == second)
            throw new ArgumentException("请选择两个不同的有效板载槽位。");
        if (a < 3 || b < 3 || a >= caps.AssignableButtonCount || b >= caps.AssignableButtonCount || a == b || shift && !caps.HasGShift)
            throw new ArgumentException("请选择两个不同的侧键；保留左键、右键和中键。");
        if (snapshot.ReadErrors.Keys.Any(s => s < caps.SectorCount)) throw new InvalidDataException("请先完整读取并备份设备。");
    }
    private static void SetEnabled(MemorySnapshot snapshot, Dictionary<ushort, bool> enabled)
    {
        var dir = (byte[])snapshot.Sectors[0].Clone();
        for (var i = 0; i < snapshot.Capabilities.ProfileCount; i++)
        {
            dir[4 * i] = 0; dir[4 * i + 1] = (byte)(i + 1); dir[4 * i + 2] = (byte)(enabled.GetValueOrDefault((ushort)(i + 1)) ? 1 : 0);
        }
        dir[4 * snapshot.Capabilities.ProfileCount] = dir[4 * snapshot.Capabilities.ProfileCount + 1] = 0xFF;
        snapshot.Sectors[0] = SectorChecksum.Seal(dir);
    }
    private static MemorySnapshot Copy(MemorySnapshot s) => new()
    {
        Format = s.Format, DeviceName = s.DeviceName, DeviceIdentity = s.DeviceIdentity, Description = (byte[])s.Description.Clone(),
        Mode = s.Mode, ActiveSector = s.ActiveSector, ReadErrors = new(s.ReadErrors),
        Sectors = s.Sectors.ToDictionary(p => p.Key, p => (byte[])p.Value.Clone())
    };
    private static void Apply(MemorySnapshot s, MemoryWritePlan plan) { foreach (var c in plan.Changes) s.Sectors[c.Sector] = c.After; }
    private static MemoryWritePlan Between(MemorySnapshot before, MemorySnapshot after, ushort active, string summary) => new(before,
        after.Sectors.Where(p => p.Key < before.Capabilities.SectorCount && !p.Value.AsSpan().SequenceEqual(before.Sectors[p.Key]))
            .OrderBy(p => p.Key == 0 ? int.MaxValue : p.Key)
            .Select(p => new SectorChange(p.Key, (byte[])before.Sectors[p.Key].Clone(), (byte[])p.Value.Clone(), p.Key == 0 ? "直达槽位目录" : "直达按键配置")).ToArray(), active, summary);
}
