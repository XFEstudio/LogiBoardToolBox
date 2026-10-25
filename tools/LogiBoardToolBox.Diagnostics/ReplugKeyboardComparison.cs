using System.Text.Json;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Core.Windows;

/// <summary>Coordinated physical comparison. No device writes or input interception.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class ReplugKeyboardComparison
{
    public static async Task RunAsync(LogitechDevice device, string folder)
    {
        if (device.Identity != "046D:C547:C972BA47:01") throw new InvalidOperationException("目标鼠标身份不匹配。");
        Directory.CreateDirectory(folder);
        if (File.Exists(Path.Combine(folder,"armed.json"))) throw new IOException("此测试目录已使用。");
        MemorySnapshot before;
        // Close HID++ handles before the physical unplug. Observation uses Raw Input.
        using (var client = LogitechDiscovery.Open(device))
            before = await new OnboardMemory(client,device.Identity,device.Name).ReadAsync();
        if (before.Mode != 1 || before.ActiveSector != 1 || before.ReadErrors.Count != 0) throw new InvalidOperationException("当前板载配置状态不适合此对照。");
        await OnboardMemory.SaveBackupAsync(before,Path.Combine(folder,"before.json"));
        using var probe = new PhysicalMacroProbe(device.Hid.ProductId,interceptExpectedOutput:false,passThroughCalibrationKey:0x31,observeDeviceChanges:true);
        await probe.Ready;
        probe.Arm(TimeSpan.FromSeconds(90));
        await Save("armed.json",new {At=DateTimeOffset.Now,Seconds=90,device.Identity,DeviceWrites=false,InputInjection=false,InputInterception=false,Calibration="Only digit 1 is passively observed by a hook; all input passes through",ReceiverKeyboardFilter="All usages from the target receiver"});
        Console.WriteLine("ARMED: passive physical replug comparison, 90 seconds.");
        while (probe.Armed)
        {
            await Task.Delay(500);
            await Save("events.json",probe.Events);
            if (File.Exists(Path.Combine(folder,"finish.request"))) break;
        }
        var events = probe.Events;
        await Save("observations.json",new {At=DateTimeOffset.Now,Events=events,
            NativeKeyboardDowns=events.Count(e=>e.Source=="raw-input"&&e.Kind=="keyboard"&&e.Down),
            CalibrationDowns=events.Count(e=>e.Source=="calibration-hook"&&e.Down),
            DeviceWrites=false,InputInjection=false,InputInterception=false});
        probe.Dispose();
        try
        {
            var found = await LogitechDiscovery.FindAsync();
            var restoredDevice=found.Single(d=>d.Identity==device.Identity);
            using var afterClient=LogitechDiscovery.Open(restoredDevice);
            var after=await new OnboardMemory(afterClient,restoredDevice.Identity,restoredDevice.Name).ReadAsync();
            await OnboardMemory.SaveBackupAsync(after,Path.Combine(folder,"after.json"));
            await Save("state-check.json",new {AllSectorsAndModeIdentical=before.Mode==after.Mode&&before.ActiveSector==after.ActiveSector&&before.Sectors.All(s=>after.Sectors.TryGetValue(s.Key,out var bytes)&&s.Value.SequenceEqual(bytes)),after.Mode,after.ActiveSector,DeviceWrites=false});
        }
        catch(Exception error) {await Save("state-check-error.json",new {Error=error.Message,DeviceWrites=false});}
        Console.WriteLine("COMPLETED: physical comparison saved.");
        Task Save(string name,object value)=>File.WriteAllTextAsync(Path.Combine(folder,name),JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));
    }
}
