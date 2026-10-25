using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Core.Windows;

if (!OperatingSystem.IsWindows()) return 1;
try
{
    string? deviceSelector = null;
    var selectorIndex = Array.IndexOf(args, "--device");
    if (selectorIndex >= 0)
    {
        if (selectorIndex + 1 >= args.Length) throw new ArgumentException("--device 后需要设备 Identity 或 UnitId。");
        deviceSelector = args[selectorIndex + 1];
        args = args.Where((_, i) => i != selectorIndex && i != selectorIndex + 1).ToArray();
    }
    if (args.Contains("--reload-onboard-runtime")) Console.WriteLine("空闲时重载板载运行状态：不写入闪存、不改键、不产生输入；恢复并核对原配置和 DPI。");
    else if (args.Any(a => a.StartsWith("--self-trigger-") || a == "--recover-keyboard-state")) Console.WriteLine("后台固件执行对照：使用未绑定实体键的临时宏区，仅保护测试功能键，结束后全量恢复。");
    else
    Console.WriteLine(args.Contains("--watch-onboard") ? "后台观察接收器输出：清理设备端按键监听；不截键、不生成输入、不改键、不写入闪存。" : args.Contains("--restore-exact") ? "恢复已备份的用户配置；先备份当前状态，恢复后全量核对。" : args.Contains("--observe-imported") ? "原生宏实测：不切换模式、不写入存储、不生成输入；开始后临时截住宏输出 30 秒。" : args.Contains("--import-ghub") ? "导入已授权的 G HUB 宏与绑定：先备份，保留录制延迟，写入后全量读回核对。" : args.Any(a => a is "--exercise-advanced" or "--prepare-click-test" or "--hero-host-check" or "--keyboard-control" or "--pure-keyboard-control" or "--native-keyboard-proof") ? "已授权的设备验证：先备份、读回校验，结束后恢复。" : "只读诊断：不切换模式、不写入设备存储。");
    foreach (var hid in WindowsHidTransport.EnumerateLogitechDevices())
        Console.WriteLine($"HID {hid.DisplayName}, Usage {hid.UsagePage:X4}:{hid.Usage:X4}, In={hid.InputReportLength}, Out={hid.OutputReportLength}");
    if (args.Contains("--ping"))
    {
        var hid = WindowsHidTransport.EnumerateLogitechDevices().First(d => d.OutputReportLength >= 20);
        using var ping = new LogiBoardToolBox.Core.HidPlusPlus.HidPlusPlusClient(new WindowsHidTransport(hid), 1)
        { Timeout = TimeSpan.FromSeconds(5), Trace = Console.WriteLine };
        Console.WriteLine("Ping=" + await ping.PingAsync());
        Console.WriteLine("Name=" + await ping.ReadNameAsync());
        return 0;
    }
    var devices = await LogitechDiscovery.FindAsync(Console.WriteLine);
    if (deviceSelector is not null)
    {
        devices = devices.Where(d => string.Equals(d.Identity, deviceSelector, StringComparison.OrdinalIgnoreCase) || string.Equals(d.UnitId, deviceSelector, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (devices.Count != 1) throw new InvalidOperationException("--device 没有唯一匹配。请使用扫描输出的完整 Identity。");
    }
    if (devices.Count == 0) { Console.WriteLine("没有发现支持 0x8100 的设备。"); return 2; }
    if (devices.Count > 1 && args.Any(a => a is "--plan-ghub" or "--import-ghub"))
        throw new InvalidOperationException("同时发现多只鼠标。请用 --device <Identity> 指定 G HUB 导入目标。");
    if (args.Length == 2 && args[0] == "--replug-keyboard-comparison")
    {
        if (devices.Count != 1) throw new InvalidOperationException("需要唯一目标鼠标。");
        await ReplugKeyboardComparison.RunAsync(devices[0],Path.GetFullPath(args[1])); return 0;
    }
    if (args.Length == 2 && args[0] == "--read-current-runtime")
    {
        if (devices.Count != 1) throw new InvalidOperationException("需要唯一目标鼠标。");
        using var client = LogitechDiscovery.Open(devices[0]);
        var memory = new OnboardMemory(client, devices[0].Identity, devices[0].Name);
        var snapshot = await memory.ReadAsync();
        var original = await OnboardMemory.LoadBackupAsync(Path.GetFullPath("work/ghub-binding-fix/keyboard-driver-correlated/probe/before.json"));
        var performance = await new LogiBoardToolBox.Core.HidPlusPlus.PerformanceController(client, true).ReadAsync();
        var output = new { At=DateTimeOffset.Now, devices[0].Identity, snapshot.Mode, snapshot.ActiveSector,
            DpiX=performance.CurrentX, DpiY=performance.CurrentY, performance.WiredRate, performance.WirelessRate,
            AllSectorsAndModeUnchanged=snapshot.Mode==original.Mode && snapshot.ActiveSector==original.ActiveSector && snapshot.Sectors.All(s=>s.Value.SequenceEqual(original.Sectors[s.Key])),
            snapshot.ReadErrors, InputGenerated=false, DeviceWrites=false };
        await File.WriteAllTextAsync(Path.GetFullPath(args[1]), System.Text.Json.JsonSerializer.Serialize(output,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("当前运行状态只读核对完成。");
        return 0;
    }
    if (args.Length == 2 && args[0] == "--read-device-properties")
    {
        if (devices.Count != 1) throw new InvalidOperationException("需要唯一目标鼠标。");
        await DevicePropertiesReadOnly.RunAsync(devices[0], Path.GetFullPath(args[1])); return 0;
    }
    if (args.Length == 2 && args[0] == "--watch-receiver-output")
    {
        if (devices.Count != 1) throw new InvalidOperationException("需要唯一目标鼠标。");
        await WatchReceiverOutput.RunAsync(devices[0], Path.GetFullPath(args[1])); return 0;
    }
    if (args.Length == 2 && args[0] is "--self-trigger-keyboard" or "--self-trigger-keyboard-active" or "--self-trigger-f1" or "--self-trigger-active-keyboard-presence" or "--self-trigger-config-complete" or "--self-trigger-native-header" or "--self-trigger-standard-key" or "--self-trigger-standard-key-long" or "--self-trigger-keyboard-presence" or "--recover-keyboard-state" or "--self-trigger-standalone-receiver" or "--self-trigger-host-mode")
    {
        if (devices.Count != 1) throw new InvalidOperationException("需要唯一目标鼠标。");
        await SelfTriggeredKeyboardExercise.RunAsync(devices[0], Path.GetFullPath(args[1]), args[0] == "--self-trigger-native-header", args[0].StartsWith("--self-trigger-standard-key"), (byte)(args[0].EndsWith("-long") ? 100 : 10), args[0] == "--self-trigger-keyboard-presence", args[0] == "--recover-keyboard-state", args[0] == "--self-trigger-standalone-receiver", args[0] == "--self-trigger-host-mode", args[0] is "--self-trigger-keyboard-active" or "--self-trigger-f1" or "--self-trigger-config-complete" or "--self-trigger-active-keyboard-presence", args[0] == "--self-trigger-f1", args[0] == "--self-trigger-config-complete", args[0] == "--self-trigger-active-keyboard-presence"); return 0;
    }
    if (args.Length == 2 && args[0] == "--reload-onboard-runtime")
    {
        if (devices.Count != 1) throw new InvalidOperationException("需要唯一目标鼠标。");
        await OnboardRuntimeReload.RunAsync(devices[0], Path.GetFullPath(args[1])); return 0;
    }
    if (args.Length == 2 && args[0] == "--audit-receiver")
    {
        if (devices.Count != 1) throw new InvalidOperationException("需要唯一目标鼠标。");
        await ReceiverReadOnlyAudit.RunAsync(devices[0], Path.GetFullPath(args[1])); return 0;
    }
    if (args.Length == 2 && args[0] == "--watch-onboard")
    {
        if (devices.Count != 1) throw new InvalidOperationException("需要唯一目标鼠标。");
        await WatchOnboardOutput.RunAsync(devices[0], Path.GetFullPath(args[1])); return 0;
    }
    if (args.Length == 3 && args[0] == "--native-keyboard-proof")
    {
        if (devices.Count != 1) throw new InvalidOperationException("需要唯一目标鼠标。");
        await NativeKeyboardExercise.RunAsync(devices[0], Path.GetFullPath(args[1]), Path.GetFullPath(args[2])); return 0;
    }
    if (args.Length == 3 && args[0] == "--restore-exact")
    {
        if (devices.Count != 1) throw new InvalidOperationException("需要唯一目标鼠标。");
        await SnapshotRestore.RunAsync(devices[0], Path.GetFullPath(args[1]), Path.GetFullPath(args[2])); return 0;
    }
    if (args.Length == 2 && args[0] is "--keyboard-control" or "--pure-keyboard-control")
    {
        if (devices.Count != 1) throw new InvalidOperationException("需要唯一目标鼠标。");
        await KeyboardControlExercise.RunAsync(devices[0], Path.GetFullPath(args[1]), args[0] == "--pure-keyboard-control"); return 0;
    }
    if (args.Length == 3 && args[0] == "--observe-imported")
    {
        if (devices.Count != 1) throw new InvalidOperationException("需要唯一目标鼠标。");
        await ImportedMacroObservation.RunAsync(devices[0], Path.GetFullPath(args[1]), Path.GetFullPath(args[2])); return 0;
    }
    if (args.Length == 3 && args[0] is "--plan-ghub" or "--import-ghub")
    {
        if (devices.Count != 1) throw new InvalidOperationException("需要唯一目标鼠标。");
        await GHubImportOperations.RunAsync(devices[0], Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args[0] == "--import-ghub"); return 0;
    }
    if (args.Length == 2 && args[0] == "--exercise-advanced") { if (devices.Count != 1) throw new InvalidOperationException("需要唯一目标鼠标。"); await HardwareExercises.AdvancedAsync(devices[0], Path.GetFullPath(args[1])); return 0; }
    if (args.Length == 2 && args[0] == "--prepare-click-test") { if (devices.Count != 1) throw new InvalidOperationException("需要唯一目标鼠标。"); await HardwareExercises.PrepareClickAsync(devices[0], Path.GetFullPath(args[1])); return 0; }
    foreach (var device in devices)
    {
        using var client = LogitechDiscovery.Open(device);
        if (args.Length == 2 && args[0] == "--hero-host-check")
        {
            var memoryCheck = new OnboardMemory(client, device.Identity, device.Name); var beforeCheck = await memoryCheck.ReadAsync();
            var advancedCheck = new LogiBoardToolBox.Core.HidPlusPlus.AdvancedMouseController(client, device.Name);
            object? hostCheck = null;
            try { await memoryCheck.SetModeAsync(false); hostCheck = await advancedCheck.ReadAsync(); }
            finally
            {
                if (beforeCheck.Mode == 1) await memoryCheck.ActivateProfileAsync(beforeCheck.ActiveSector); else await memoryCheck.SetModeAsync(false);
                var afterCheck = await memoryCheck.ReadAsync();
                await File.WriteAllTextAsync(args[1], System.Text.Json.JsonSerializer.Serialize(new { SoftwareModeState = hostCheck, OriginalMode = beforeCheck.Mode, RestoredMode = afterCheck.Mode, RestoredActiveSector = afterCheck.ActiveSector, AllSectorsIdentical = beforeCheck.Sectors.All(p => p.Value.SequenceEqual(afterCheck.Sectors[p.Key])) }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            continue;
        }
        if (args.Contains("--advanced"))
        {
            client.Trace = Console.WriteLine;
            var controller = new LogiBoardToolBox.Core.HidPlusPlus.AdvancedMouseController(client, device.Name);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(await controller.ReadAsync()));
            continue;
        }
        if (args.Contains("--status")) { Console.WriteLine(await LogiBoardToolBox.Core.HidPlusPlus.DeviceStatusReader.ReadAsync(client)); continue; }
        if (args.Contains("--features"))
        {
            foreach (var id in new ushort[] { 0x0001, 0x2201, 0x2202, 0x8060, 0x8061, 0x8070, 0x8071, 0x8081, 0x8110, 0x1B04 })
            {
                var index = await client.GetFeatureAsync(id);
                Console.WriteLine($"Feature {id:X4} = {index:X2}");
                if (index == 0) continue;
                var calls = id switch
                {
                    0x2201 => new (byte Function, byte[] Data)[] { (0, []), (1, [0]), (2, [0]) },
                    0x2202 => [(0, []), (1, [0]), (2, [0, 0]), (2, [0, 1]), (3, [0, 0]), (5, [0])],
                    0x8060 => [(0, []), (1, [])],
                    0x8061 => [(0, []), (1, []), (2, [])],
                    0x8110 => [(0, []), (3, [])],
                    0x8071 => [(0, [0xFF, 0xFF, 0]), (0, [0, 0xFF, 0]), (0, [1, 0xFF, 0])],
                    0x8081 => [(0, [0, 0]), (0, [0, 1]), (0, [0, 2])],
                    _ => []
                };
                foreach (var (fn, data) in calls)
                {
                    try { Console.WriteLine($"  {fn:X1} {Convert.ToHexString(data)} -> {Convert.ToHexString(await client.CallAsync(index, fn, data))}"); }
                    catch (Exception ex) { Console.WriteLine("  " + ex.Message); }
                }
                if (id == 0x8071)
                {
                    for (byte z = 0; z < 2; z++)
                        for (byte e = 0; e < 24; e++)
                        {
                            try { var data = await client.CallAsync(index, 0, [z, e, 0]); Console.WriteLine($"  zone {z} effect {e}: {Convert.ToHexString(data)}"); }
                            catch (LogiBoardToolBox.Core.HidPlusPlus.HidPlusPlusException) { break; }
                        }
                }
            }
            continue;
        }
        var memory = new OnboardMemory(client, device.Identity, device.Name);
        var snapshot = await memory.ReadAsync();
        Console.WriteLine($"{device.DisplayName}\n{snapshot.Capabilities.Summary}\n模式={snapshot.Mode}, 活动扇区={snapshot.ActiveSector}, 已读扇区={snapshot.Sectors.Count}, 读取错误={snapshot.ReadErrors.Count}");
        foreach (var error in snapshot.ReadErrors) Console.WriteLine($"读取错误 0x{error.Key:X4}: {error.Value}");
        foreach (var profile in snapshot.GetProfiles())
        {
            var rate = $"有线 {profile.GetRateHz(snapshot.Capabilities, false)} Hz / 无线 {profile.GetRateHz(snapshot.Capabilities, true)} Hz";
            Console.WriteLine($"{profile.DisplayName}, 出厂回退={profile.FromFactory}, DPI X={string.Join(',', profile.Dpis)}, DPI Y={string.Join(',', profile.GetDpisY(snapshot.Capabilities))}, 回报率={rate}");
            for (var i = 0; i < snapshot.Capabilities.AssignableButtonCount; i++)
            {
                var label = device.Name.Contains("G502 X", StringComparison.OrdinalIgnoreCase)
                    ? i switch { 11 => "滚轮向下", 12 => "滚轮向上", _ => $"G{i + 1}" } : $"按键 {i + 1}";
                Console.WriteLine($"  {label}: {Convert.ToHexString(profile.GetBinding(i))} {ButtonBinding.Describe(profile.GetBinding(i))}");
            }
        }
        if (args.Length == 2 && args[0] == "--backup")
        {
            await OnboardMemory.SaveBackupAsync(snapshot, args[1]);
            Console.WriteLine("只读备份已保存：" + Path.GetFullPath(args[1]));
        }
    }
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
