using System.Buffers.Binary;
using LogiBoardToolBox.Core.HidPlusPlus;

namespace LogiBoardToolBox.ViewModels;

public partial class MainPageViewModel
{
    private ushort? reportedDpiSector;
    private byte? reportedDpiIndex;
    private ushort ActiveDpiSector => reportedDpiSector ?? snapshot?.ActiveSector ?? 0;

    private void SetDeviceClient(HidPlusPlusClient? connection)
    {
        if (client is { } previous)
        {
            previous.Notification -= OnDpiNotification;
            previous.Dispose();
        }
        client = connection;
        reportedDpiSector = null;
        reportedDpiIndex = null;
        if (connection is not null) connection.Notification += OnDpiNotification;
    }

    private void OnDpiNotification(object? sender, HidNotification notice)
    {
        if (sender is not HidPlusPlusClient connection) return;
        var onboard = notice.Function is 0 or 1 && connection.IsNotificationFor(0x8100, notice);
        var sensorChanged = notice.Function == 0 && connection.IsNotificationFor(0x2202, notice);
        if (!onboard && !sensorChanged) return;
        ui?.TryEnqueue(() =>
        {
            if (shuttingDown || !ReferenceEquals(connection, client) || snapshot is null) return;
            if (onboard) ApplyOnboardDpiNotice(notice);
            else if (notice.Data is [0, _, _, _, _, _, ..])
            {
                var x = BinaryPrimitives.ReadUInt16BigEndian(notice.Data.AsSpan(1));
                var y = BinaryPrimitives.ReadUInt16BigEndian(notice.Data.AsSpan(3));
                PublishCurrentDpi(x, y == 0 ? x : y, notice.Data[5]);
            }
        });
    }

    private void ApplyOnboardDpiNotice(HidNotification notice)
    {
        if (snapshot?.Mode != 1) return;
        if (notice.Function == 0 && notice.Data.Length >= 2)
        {
            var sector = BinaryPrimitives.ReadUInt16BigEndian(notice.Data);
            if (sector == 0 || sector == ActiveDpiSector) return;
            reportedDpiSector = sector;
            reportedDpiIndex = null;
            UpdateCurrentDpiStage();
        }
        else if (notice.Function == 1 && notice.Data is [< 5, ..])
        {
            var index = notice.Data[0];
            var profile = Profiles.FirstOrDefault(p => p.Sector == ActiveDpiSector);
            if (profile is null || profile.Dpis[index] is 0 or ushort.MaxValue) return;
            reportedDpiIndex = index;
            PublishCurrentDpi(profile.Dpis[index], profile.GetDpisY(snapshot.Capabilities)[index]);
        }
    }

    private void PublishCurrentDpi(ushort x, ushort y, byte? lod = null)
    {
        if (x is 0 or ushort.MaxValue || y is 0 or ushort.MaxValue) return;
        if (sensor is { } previous)
            sensor = previous with { CurrentX = x, CurrentY = y, Lod = lod ?? previous.Lod };
        performance?.ObserveDpi(x, y, lod);
        UpdateCurrentDpiStage();
        PerformanceSummary = sensor is { } state
            ? $"当前 {x} × {y} DPI · {(SelectedLiveRate?.Label ?? "回报率未提供")} · 传感器 {state.Minimum}–{state.Maximum} DPI"
            : $"当前 {x} × {y} DPI";
    }

    private void UpdateCurrentDpiStage()
    {
        DpiStageEditor? current = null;
        var matchesBoard = IsEditingOnboard && snapshot?.Mode == 1 && SelectedProfile?.Sector == ActiveDpiSector;
        if (matchesBoard || IsEditingSoftware)
        {
            var matches = DpiStages.Where(s => s.Enabled && sensor is { } state &&
                ushort.TryParse(s.Value, out var x) && x == state.CurrentX &&
                ushort.TryParse(s.LinkAxes ? s.Value : s.ValueY, out var y) && y == state.CurrentY).ToArray();
            if (matchesBoard && reportedDpiIndex is { } index)
                current = sensor is null ? DpiStages.FirstOrDefault(s => s.Index == index && s.Enabled) : matches.FirstOrDefault(s => s.Index == index);
            if (current is null && matches.Length == 1) current = matches[0];
        }
        foreach (var stage in DpiStages) stage.IsCurrent = ReferenceEquals(stage, current);
    }
}
