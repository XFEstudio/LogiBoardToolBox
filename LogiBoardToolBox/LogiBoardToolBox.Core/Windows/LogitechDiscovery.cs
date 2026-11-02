using System.Runtime.Versioning;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Devices;
using LogiBoardToolBox.Core.Diagnostics;

namespace LogiBoardToolBox.Core.Windows;

public sealed record LogitechDevice(HidDeviceInfo Hid, byte DeviceIndex, string Name, string UnitId, IReadOnlyList<ushort>? TransportProductIds = null,
    bool HasOnboardProfiles = true)
{
    public MouseModel Model => MouseCatalog.Resolve(TransportProductIds ?? [Hid.ProductId], Name);
    public string DisplayName => $"{Name} · {(DeviceIndex == 0xFF ? "USB" : "LIGHTSPEED")} · {Hid.ProductId:X4}";
    public string Identity => $"046D:{Hid.ProductId:X4}:{UnitId}:{DeviceIndex:X2}";
    public override string ToString() => DisplayName;
}

[SupportedOSPlatform("windows")]
public static class LogitechDiscovery
{
    public static async Task<IReadOnlyList<LogitechDevice>> FindAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        var devices = new List<LogitechDevice>();
        var interfaces = WindowsHidTransport.EnumerateLogitechDevices();
        DiagnosticLog.Record("discovery", "interfaces", interfaces);
        foreach (var hid in interfaces.Where(d => d.OutputReportLength >= 20).OrderBy(d => d.OutputReportLength))
        {
            log?.Invoke($"探测 {hid.DisplayName} / {hid.UsagePage:X4}:{hid.Usage:X4}");
            foreach (byte index in HidPlusPlusDeviceProbe.DeviceIndices(hid))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var client = new HidPlusPlusClient(new WindowsHidTransport(hid), index);
                    DiagnosticLog.Record("discovery", "probe started", new { hid.Path, DeviceIndex = index, client.DiagnosticId });
                    client.Timeout = TimeSpan.FromMilliseconds(index == 1 ? 2500 : 650);
                    var device = await HidPlusPlusDeviceProbe.ProbeAsync(client, hid, ct).ConfigureAwait(false);
                    if (device is null) continue;
                    if (devices.All(d => d.Identity != device.Identity)) devices.Add(device);
                    DiagnosticLog.Record("discovery", "device found", device);
                    log?.Invoke($"已找到 {device.DisplayName}" + (device.HasOnboardProfiles ? "" : "（未提供标准板载接口）"));
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or System.ComponentModel.Win32Exception)
                {
                    DiagnosticLog.Record("discovery", "probe failed", new { hid.Path, DeviceIndex = index }, ex);
                    log?.Invoke($"地址 {index:X2}: {ex.Message}");
                }
            }
        }
        DiagnosticLog.Record("discovery", "completed", devices);
        return devices;
    }

    public static HidPlusPlusClient Open(LogitechDevice device) => new(new WindowsHidTransport(device.Hid), device.DeviceIndex);
}
