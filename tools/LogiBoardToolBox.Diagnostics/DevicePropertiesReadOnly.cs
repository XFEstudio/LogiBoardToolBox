using System.Security.Cryptography;
using System.Text.Json;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Core.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class DevicePropertiesReadOnly
{
    public static async Task RunAsync(LogitechDevice device, string folder)
    {
        if (device.Identity != "046D:C547:C972BA47:01") throw new InvalidOperationException("只读取已核对的目标设备。");
        Directory.CreateDirectory(folder);
        using var client = LogitechDiscovery.Open(device);
        var memory = new OnboardMemory(client, device.Identity, device.Name);
        var before = await memory.ReadAsync();
        // Native vtable 1DB35F0 returns Fn0's first byte. Native 1DB42B0
        // sends two selector bytes to Fn5 and copies reply bytes to outputs.
        // No other 1806 functions are called (Fn1/2/3/4 are mutating).
        var info = await client.FeatureAsync(0x1806, 0);
        var records = new List<object>();
        for (byte property = 0; property < Math.Min(32, (int)info[0]); property++)
        {
            try
            {
                var value = await client.FeatureAsync(0x1806, 5, [property, 0]);
                records.Add(new { Selector = property, ReturnedSelector = value[0], FlagsAndSize = value[1], Data = Convert.ToHexString(value[2..]) });
            }
            catch (Exception error) { records.Add(new { Selector = property, Error = error.Message }); break; }
        }
        var after = await memory.ReadAsync();
        var unchanged = before.Mode == after.Mode && before.ActiveSector == after.ActiveSector && before.Sectors.All(p => p.Value.SequenceEqual(after.Sectors[p.Key]));
        await File.WriteAllTextAsync(Path.Combine(folder, "properties.json"), JsonSerializer.Serialize(new
        { Info = Convert.ToHexString(info), Records = records, AllSectorsAndModeUnchanged = unchanged, ReadFunctionsOnly = new[] { 0, 5 } }, new JsonSerializerOptions { WriteIndented = true }));
        if (!unchanged) throw new IOException("只读检查期间设备状态改变。");
        Console.WriteLine($"只读属性检查完成：{records.Count} 条结果；配置未改变。");
    }
}
