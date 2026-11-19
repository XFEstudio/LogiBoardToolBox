using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Windows;

namespace LogiBoardToolBox.Core.Devices;

/// <summary>Read-only identity probing, independent of Windows handles and flash-layout support.</summary>
public static class HidPlusPlusDeviceProbe
{
    public static IReadOnlyList<byte> DeviceIndices(HidDeviceInfo hid) =>
        hid.ProductId is >= 0xC000 and < 0xC400 && MouseCatalog.Resolve([hid.ProductId], "").IsKnown
            ? [0xFF] // A known mouse's cable interface is direct; it is not a six-slot receiver.
            : [1, 0xFF, 2, 3, 4, 5, 6];

    public static async Task<LogitechDevice?> ProbeAsync(HidPlusPlusClient client, HidDeviceInfo hid, CancellationToken ct = default)
    {
        if (!await client.PingAsync(ct).ConfigureAwait(false)) return null;
        var name = await client.ReadNameAsync(ct).ConfigureAwait(false);
        if (name == "Logitech HID++ Mouse" || string.IsNullOrWhiteSpace(name)) name = hid.ProductName;
        var unit = hid.SerialNumber;
        IReadOnlyList<ushort> ids = [hid.ProductId];
        if (await client.GetFeatureAsync(0x0003, ct).ConfigureAwait(false) != 0)
        {
            var info = await client.FeatureAsync(0x0003, 0, ct: ct).ConfigureAwait(false);
            var reported = MouseCatalog.ReadTransportProductIds(info);
            if (reported.Count > 0)
            {
                ids = reported;
                // Some older firmware returns incomplete transport identity.
                // Retain a verified physical cable PID if *none* of the reported
                // IDs identifies a model. Never use a shared receiver PID or
                // override a recognised identity reported by the mouse.
                if (!reported.Any(id => MouseCatalog.Resolve([id], "").IsKnown) &&
                    hid.ProductId is >= 0xC000 and < 0xC400 && MouseCatalog.Resolve([hid.ProductId], "").IsKnown)
                    ids = reported.Append(hid.ProductId).Distinct().ToArray();
            }
            if (info.Length >= 5 && info.AsSpan(1, 4).IndexOfAnyExcept((byte)0) >= 0)
                unit = Convert.ToHexString(info, 1, 4);
        }
        var hasProfiles = await client.GetFeatureAsync(0x8100, ct).ConfigureAwait(false) != 0;
        // Firmware/profile support is a separate question from mouse identity. A known
        // reachable mouse must remain visible even when its profile feature is absent.
        // Unknown HID++ keyboards and receiver roots are not promoted to mice.
        if (!hasProfiles && !MouseCatalog.Resolve(ids, name).IsKnown) return null;
        if (string.IsNullOrEmpty(unit)) unit = hid.Path.ToUpperInvariant();
        return new(hid, client.DeviceIndex, name, unit, ids, hasProfiles);
    }
}
