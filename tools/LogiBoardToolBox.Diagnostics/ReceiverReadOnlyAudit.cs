using System.Text.Json;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Core.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class ReceiverReadOnlyAudit
{
    public static async Task RunAsync(LogitechDevice device, string folder)
    {
        Directory.CreateDirectory(folder);
        var trace = new List<string>();
        var features = new List<object>(); var firmware = new List<string>();
        MemorySnapshot before, after; string? spyMap = null;
        using (var client = LogitechDiscovery.Open(device))
        {
            client.Trace = trace.Add;
            var memory = new OnboardMemory(client, device.Identity, device.Name);
            before = await memory.ReadAsync();
            var count = (await client.FeatureAsync(0x0001, 0))[0];
            for (var index = 1; index <= count; index++)
            {
                var info = await client.FeatureAsync(0x0001, 1, [(byte)index]);
                features.Add(new { Index = index.ToString("X2"), Id = Convert.ToHexString(info, 0, 2), Flags = info[2], Version = info.Length > 3 ? info[3] : 0 });
            }
            var infoCount = (await client.FeatureAsync(0x0003, 0))[0];
            for (byte index = 0; index < infoCount; index++) firmware.Add(Convert.ToHexString(await client.FeatureAsync(0x0003, 1, [index])));
            if (await client.GetFeatureAsync(0x8110) != 0) spyMap = Convert.ToHexString(await client.FeatureAsync(0x8110, 3));
            after = await memory.ReadAsync();
        }
        var registers = new List<object>();
        if (device.DeviceIndex != 0xFF)
        {
            var shortInterface = WindowsHidTransport.EnumerateLogitechDevices().Single(d => d.CollectionGroup == device.Hid.CollectionGroup && d.OutputReportLength == 7);
            // Only documented receiver GET_REGISTER commands. No notification flags,
            // pairing, HID reports, mode or firmware state are changed here.
            using var transport = new WindowsHidTransport(shortInterface);
            // GET_LONG_REGISTER is sent on the short collection but replies on
            // the long one. Listen to both; never send a pairing/write command.
            using var receiverInput = new WindowsHidTransport(device.Hid);
            foreach (var (command, register, selector) in new (byte, byte, byte)[]
            {
                (0x81, 0, 0), (0x81, 2, 0), (0x81, 0xF1, 1), (0x81, 0xF1, 2), (0x81, 0xF1, 3), (0x81, 0xF1, 4), (0x81, 0xF1, 0x11),
                (0x83, 0xB5, 3), (0x83, 0xB5, (byte)(0x20 + device.DeviceIndex - 1)), (0x83, 0xB5, (byte)(0x30 + device.DeviceIndex - 1))
            })
            {
                byte[] request = [0x10, 0xFF, command, register, selector, 0, 0];
                trace.Add("TX " + Convert.ToHexString(request));
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await transport.WriteAsync(request, timeout.Token);
                    while (true)
                    {
                        var reply = await receiverInput.ReadAsync(timeout.Token); trace.Add("RX " + Convert.ToHexString(reply));
                        if (reply.Length < 7 || reply[1] != 0xFF) continue;
                        if (reply[2] == command && reply[3] == register || reply[2] == 0x8F && reply[3] == command && reply[4] == register)
                        {
                            registers.Add(new { Register = register.ToString("X2"), Selector = selector.ToString("X2"), Reply = Convert.ToHexString(reply) }); break;
                        }
                    }
                }
                catch (OperationCanceledException) { registers.Add(new { Register = register.ToString("X2"), Selector = selector.ToString("X2"), Error = "Timed out" }); }
            }
        }
        using (var client = LogitechDiscovery.Open(device))
            after = await new OnboardMemory(client, device.Identity, device.Name).ReadAsync();
        var unchanged = before.Sectors.All(p => p.Value.SequenceEqual(after.Sectors[p.Key])) && before.Mode == after.Mode && before.ActiveSector == after.ActiveSector;
        await File.WriteAllLinesAsync(Path.Combine(folder, "read-only-trace.log"), trace);
        await File.WriteAllTextAsync(Path.Combine(folder, "audit.json"), JsonSerializer.Serialize(new
        {
            device.Identity, Features = features, DeviceFirmware = firmware, ReceiverRegisters = registers, SpyMap = spyMap,
            AllSectorsAndModeUnchanged = unchanged, after.Mode, after.ActiveSector,
            InputGenerated = false, HooksInstalled = false, PairingChanged = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (!unchanged) throw new IOException("只读检查期间设备配置发生了变化。");
        Console.WriteLine($"只读检查完成：{features.Count} 个设备功能；全部扇区、模式和活动配置保持一致。");
    }
}
