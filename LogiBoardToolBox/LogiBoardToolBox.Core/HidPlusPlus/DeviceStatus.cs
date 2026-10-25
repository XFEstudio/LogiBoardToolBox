using System.Buffers.Binary;

namespace LogiBoardToolBox.Core.HidPlusPlus;

public sealed record DeviceStatus(int? Battery, byte Charging, string Firmware, string FirmwareRaw);

public static class DeviceStatusReader
{
    public static async Task<DeviceStatus> ReadAsync(HidPlusPlusClient client, CancellationToken cancellationToken = default)
    {
        int? battery = null; byte charging = 255; string firmware = "", raw = "";
        var power = await client.GetFeatureAsync(0x1004, cancellationToken);
        if (power != 0) { var report = await client.CallAsync(power, 1, [], cancellationToken); if (report[0] <= 100) battery = report[0]; charging = report[2]; }
        else
        {
            power = await client.GetFeatureAsync(0x1000, cancellationToken);
            if (power != 0) { var report = await client.CallAsync(power, 0, [], cancellationToken); if (report[0] <= 100) battery = report[0]; charging = report[2]; }
        }
        var version = await client.GetFeatureAsync(0x0003, cancellationToken);
        if (version != 0)
        {
            var count = (await client.CallAsync(version, 0, [], cancellationToken))[0];
            for (byte i = 0; i < Math.Min(16, (int)count); i++)
            {
                var entity = await client.CallAsync(version, 1, [i], cancellationToken);
                if ((entity[0] & 15) != 0) continue;
                raw = Convert.ToHexString(entity); firmware = $"{entity[4]:X}.{entity[5]:X}.{BinaryPrimitives.ReadUInt16BigEndian(entity.AsSpan(6, 2)):X}"; break;
            }
        }
        return new(battery, charging, firmware, raw);
    }
}
