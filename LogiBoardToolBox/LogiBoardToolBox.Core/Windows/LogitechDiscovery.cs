using System.Runtime.Versioning;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Devices;

namespace LogiBoardToolBox.Core.Windows;

public sealed record LogitechDevice(HidDeviceInfo Hid, byte DeviceIndex, string Name, string UnitId, IReadOnlyList<ushort>? TransportProductIds = null)
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
        foreach (var hid in WindowsHidTransport.EnumerateLogitechDevices().Where(d => d.OutputReportLength >= 20).OrderBy(d => d.OutputReportLength))
        {
            log?.Invoke($"探测 {hid.DisplayName} / {hid.UsagePage:X4}:{hid.Usage:X4}");
            foreach (byte index in new byte[] { 1, 0xFF, 2, 3, 4, 5, 6 })
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var client = new HidPlusPlusClient(new WindowsHidTransport(hid), index);
                    client.Timeout = TimeSpan.FromMilliseconds(index == 1 ? 2500 : 650);
                    if (!await client.PingAsync(ct).ConfigureAwait(false)) continue;
                    if (await client.GetFeatureAsync(0x8100, ct).ConfigureAwait(false) == 0) continue;
                    var name = await client.ReadNameAsync(ct).ConfigureAwait(false);
                    var unit = hid.SerialNumber;
                    IReadOnlyList<ushort> transportIds = [hid.ProductId];
                    if (await client.GetFeatureAsync(0x0003, ct).ConfigureAwait(false) != 0)
                    {
                        var info = await client.FeatureAsync(0x0003, 0, ct: ct).ConfigureAwait(false);
                        var reportedIds = MouseCatalog.ReadTransportProductIds(info);
                        if (reportedIds.Count > 0) transportIds = reportedIds;
                        if (info.Length >= 5 && info.AsSpan(1, 4).IndexOfAnyExcept((byte)0) >= 0)
                            unit = Convert.ToHexString(info, 1, 4);
                    }
                    if (string.IsNullOrEmpty(unit)) unit = hid.Path.ToUpperInvariant();
                    var device = new LogitechDevice(hid, index, name, unit, transportIds);
                    if (devices.All(d => d.Identity != device.Identity)) devices.Add(device);
                    log?.Invoke($"已找到 {device.DisplayName}");
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or System.ComponentModel.Win32Exception)
                {
                    log?.Invoke($"地址 {index:X2}: {ex.Message}");
                }
            }
        }
        return devices;
    }

    public static HidPlusPlusClient Open(LogitechDevice device) => new(new WindowsHidTransport(device.Hid), device.DeviceIndex);
}
