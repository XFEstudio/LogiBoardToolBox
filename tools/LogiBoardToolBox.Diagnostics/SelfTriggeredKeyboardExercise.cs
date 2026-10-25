using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Core.Windows;

/// <summary>Short firmware-executed F24 probe; existing bindings are never changed.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class SelfTriggeredKeyboardExercise
{
    public static async Task RunAsync(LogitechDevice device, string folder, bool nativeHeader = false, bool standardFunctionKey = false, byte holdMs = 10, bool keyboardPresence = false, bool releaseKeyboard = false, bool standaloneReceiver = false, bool hostMode = false, bool activeSafe = false, bool firstFunctionKey = false, bool completeConfiguration = false, bool activeKeyboardPresence = false)
    {
        if (activeSafe && (nativeHeader || standardFunctionKey || keyboardPresence || releaseKeyboard || standaloneReceiver || hostMode))
            throw new ArgumentException("免空闲对照仅允许独立功能键测试。");
        standardFunctionKey |= nativeHeader;
        if (device.Identity != "046D:C547:C972BA47:01") throw new InvalidOperationException("仅测试已核对的目标鼠标。");
        Directory.CreateDirectory(folder);
        if (File.Exists(Path.Combine(folder, "prepared.json"))) throw new IOException("诊断目录已经使用。");
        using var client = LogitechDiscovery.Open(device);
        var trace = new ConcurrentQueue<string>(); client.Trace = trace.Enqueue;
        var memory = new OnboardMemory(client, device.Identity, device.Name);
        var before = await memory.ReadAsync();
        if (before.Mode != 1 || before.ActiveSector != 1 || before.ReadErrors.Count != 0)
            throw new InvalidOperationException("当前状态不适合无干扰诊断。");
        await OnboardMemory.SaveBackupAsync(before, Path.Combine(folder, "before.json"));
        const int sector = 6, offset = 128;
        // Known native macro sector; use only its unreferenced erased tail. The
        // two-byte entry alignment and original CRC convention are retained.
        if (!before.Sectors[sector].AsSpan(offset, 48).ToArray().All(b => b == 0xFF))
            throw new InvalidOperationException("测试位置不是空白。");
        foreach (var profile in before.GetProfiles())
        for (var layer = 0; layer < 2; layer++)
        for (var button = 0; button < before.Capabilities.BindingRecordCount; button++)
        {
            var binding = profile.GetBinding(button, layer == 1);
            if (ButtonBinding.IsMacro(binding) && ButtonBinding.MacroSector(binding) == sector
                && ButtonBinding.MacroOffset(binding) >= offset)
                throw new InvalidOperationException("现有绑定指向测试区。");
        }
        // Existing target programs contain no jump into their erased tail.
        if (before.Sectors[sector].AsSpan(0, offset).Contains((byte)0x60))
            throw new InvalidOperationException("存在需要另行分析的宏跳转。");
        var candidate = (byte[])before.Sectors[sector].Clone();
        var usage = (byte)(firstFunctionKey ? 0x3A : standardFunctionKey ? 0x41 : 0x73); // F1 / F8 / F24
        var virtualKey = firstFunctionKey ? 0x70 : standardFunctionKey ? 0x77 : 0x87;
        var keyName = firstFunctionKey ? "F1" : standardFunctionKey ? "F8" : "F24";
        byte[] code = [0x43, 0, usage, 0x40, 0, holdMs, 0x44, 0, usage, 0x40, 0, 10, 0xFF, 0xFF];
        code.CopyTo(candidate, offset);
        // Consumer Control usage 1 is the collection itself, with no assigned
        // OS command; zero movement likewise leaves pointer and wheel unchanged.
        byte[] consumerControl = [0x45, 0, 1, 0x40, 0, 100, 0x46, 0, 1, 0xFF];
        consumerControl.CopyTo(candidate, 144);
        byte[] zeroMovement = [0x61, 0, 0, 0, 0, 0xFF];
        zeroMovement.CopyTo(candidate, 160);
        candidate = SectorChecksum.Seal(candidate);
        var changes = new List<SectorChange> { new(sector, before.Sectors[sector], candidate, "临时功能键单次诊断，不绑定实体键") };
        if (releaseKeyboard)
        {
            var referenced = MacroAllocator.FindReferencedSectors(before);
            var releases = Enumerable.Range(4, 112).Select(key => new byte[] { 0x44, 0, (byte)key })
                .Concat(Enumerable.Range(0, 8).Select(bit => new byte[] { 0x44, (byte)(1 << bit), (byte)(0xE0 + bit) })).ToArray();
            for (var part = 0; part < 2; part++)
            {
                var page = 9 + part;
                if (referenced.Contains(page) || !before.Sectors[page].All(b => b == 0xFF))
                    throw new InvalidOperationException("清理程序所需的临时扇区不是空白或已被引用。");
                var data = Enumerable.Repeat((byte)0xFF, before.Capabilities.SectorSize).ToArray();
                releases.Skip(part * 60).Take(60).SelectMany(b => b).ToArray().CopyTo(data, 0);
                SectorChecksum.Seal(data);
                changes.Insert(part, new(page, before.Sectors[page], data, "临时纯松开指令，结束恢复空白扇区"));
            }
        }
        if (nativeHeader)
        {
            var native = await OnboardMemory.LoadBackupAsync(Path.GetFullPath("work/ghub-binding-fix/native-ghub-mode/native-once-final-encoded.json"));
            if (native.DeviceIdentity != before.DeviceIdentity || !native.Description.Take(11).SequenceEqual(before.Description.Take(11)))
                throw new InvalidDataException("原生对照不属于目标设备。");
            var profile = (byte[])before.Sectors[before.ActiveSector].Clone();
            native.Sectors[3].AsSpan(22, 4).CopyTo(profile.AsSpan(22, 4));
            SectorChecksum.Seal(profile);
            changes.Add(new(before.ActiveSector, before.Sectors[before.ActiveSector], profile, "仅对照原生头字段 22–25，结束恢复"));
        }
        if (keyboardPresence)
        {
            var profile = before.GetProfiles().Single(p => p.Sector == before.ActiveSector).Copy();
            for (var button = 0; button < before.Capabilities.BindingRecordCount; button++)
            {
                var binding = profile.GetBinding(button);
                if (binding[0] == 0x90 && binding[1] == 11)
                    throw new InvalidOperationException("普通层存在 G-Shift 入口，不能用此无入口层作对照。");
            }
            profile.SetBinding(10, true, ButtonBinding.Keyboard(0, 0x73));
            changes.Add(new(before.ActiveSector, before.Sectors[before.ActiveSector], profile.ToBytes(), "仅在无法进入的 G-Shift 层对照直接键盘绑定，结束恢复"));
        }
        if (activeKeyboardPresence)
        {
            var profile = before.GetProfiles().Single(p => p.Sector == before.ActiveSector).Copy();
            if (!profile.GetBinding(4).SequenceEqual(ButtonBinding.Macro(8, 0))) throw new InvalidOperationException("取消刀实体键的现有绑定与预期不符。");
            profile.SetBinding(4, false, ButtonBinding.Keyboard(0, 0x73));
            changes.Add(new(before.ActiveSector, before.Sectors[before.ActiveSector], profile.ToBytes(), "临时将故障的 G5 设为 F24，结束恢复原取消刀"));
        }
        var plan = new MemoryWritePlan(before, changes, before.ActiveSector, "后台短时固件诊断");
        await Save("prepared.json", new { device.Identity, Sector = sector, Offset = offset, Bytecode = Convert.ToHexString(code),
            PrimaryBindingsChanged = activeKeyboardPresence, ActiveKeyboardPresence=activeKeyboardPresence, InactiveGShiftProbeBinding = keyboardPresence, ReleaseOnlyRecovery = releaseKeyboard, NativeHeaderFields = nativeHeader, NativeExecutionFunction = "8100/9", TestVirtualKey = virtualKey,
            NormalKeysIntercepted = false, MouseIntercepted = false, SoftwareInputGeneration = false,
            RestoreAllSectorsAtEnd = true, WaitForInputIdle = !activeSafe });
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!activeSafe && (IdleMilliseconds() < 5000 || AnyInputHeld()))
        {
            if (DateTime.UtcNow >= deadline) { await Save("skipped.json", new { Reason = "No idle interval", NoDeviceChanges = true }); return; }
            await Task.Delay(250);
        }
        var touched = false;
        var results = new List<object>();
        ReceiverNotificationScope? notificationScope = null;
        byte[]? originalConfigurationCookie = null;
        var switchedMode = false;
        try
        {
            touched = true;
            await memory.CommitAsync(plan, Path.Combine(folder, "backups"));
            if (completeConfiguration)
            {
                // Solaar's ConfigChange GetCookie/SetComplete handshake. This is
                // configuration completion, with no factory or debug commands.
                originalConfigurationCookie = (await client.FeatureAsync(0x0020, 0))[..2];
                await client.FeatureAsync(0x0020, 1, [0x4C, 0x42]);
                var acknowledged = (await client.FeatureAsync(0x0020, 0))[..2];
                await Save("configuration-completion.json", new { OriginalCookie=Convert.ToHexString(originalConfigurationCookie), AcknowledgedCookie=Convert.ToHexString(acknowledged) });
            }
            if (standaloneReceiver)
            {
                notificationScope = new(device);
                await notificationScope.ClearSoftwarePresentAsync();
                await Save("receiver-notifications-during.json", new { Flags = Convert.ToHexString(await notificationScope.ReadAsync()), OnlySoftwarePresentBitChanged = true });
            }
            // Profile activation itself emits an all-released consumer report,
            // which updates LASTINPUTINFO. Wait for that report to age out.
            var executionDeadline = DateTime.UtcNow.AddSeconds(60);
            while (!activeSafe && (IdleMilliseconds() < 5000 || AnyInputHeld()))
            {
                if (DateTime.UtcNow >= executionDeadline)
                { await Save("skipped.json", new { Reason = "No idle interval after install", MacroExecuted = false }); return; }
                await Task.Delay(250);
            }
            if (releaseKeyboard)
            {
                using var releaseProbe = new PhysicalMacroProbe(device.Hid.ProductId, interceptExpectedOutput: false);
                await releaseProbe.Ready; releaseProbe.Arm(TimeSpan.FromSeconds(1));
                for (byte page = 9; page <= 10; page++)
                {
                    await client.FeatureAsync(0x8100, 9, [0, page, 0, 0]);
                    await Task.Delay(160);
                }
                await Save("release-only.json", new { Events = releaseProbe.Events, KeyDownInstructions = 0, KeyboardUsagesReleased = 120 });
                executionDeadline = DateTime.UtcNow.AddSeconds(60);
                while (IdleMilliseconds() < 5000 || AnyInputHeld())
                {
                    if (DateTime.UtcNow >= executionDeadline) return;
                    await Task.Delay(250);
                }
            }
            using var probe = new PhysicalMacroProbe(device.Hid.ProductId, interceptExpectedOutput: false, diagnosticGuardKey: virtualKey);
            await probe.Ready; probe.Arm(TimeSpan.FromMilliseconds(500));
            if (hostMode)
            {
                switchedMode = true;
                await client.FeatureAsync(0x8100, 1, [2]);
            }
            foreach (var (address, name) in new[] { (offset, keyName), (144, "Consumer collection (no OS action)") })
            {
                if (!probe.Armed || (!activeSafe && (IdleMilliseconds() < 5000 || AnyInputHeld()))) break;
                try
                {
                    var reply = await client.FeatureAsync(0x8100, 9, [0, sector, 0, (byte)address]);
                    results.Add(new { Test = name, Success = true, Reply = Convert.ToHexString(reply) });
                }
                catch (Exception error) { results.Add(new { Test = name, Success = false, Error = error.Message }); }
                await Task.Delay(120);
            }
            await Task.Delay(100);
            await Save("execution.json", new { At = DateTimeOffset.Now, Results = results, Events = probe.Events,
                NativeKeyboardDowns = probe.Events.Count(e => e.Source == "raw-input" && e.Kind == "keyboard" && e.Down),
                GuardedTestKeyDowns = probe.Events.Count(e => e.Source == "low-level-hook" && e.Kind == "keyboard" && e.Down),
                InputInjectedBySoftware = false, PrimaryBindingsChanged = activeKeyboardPresence });
        }
        finally
        {
            try
            {
                if (switchedMode)
                {
                    using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await client.FeatureAsync(0x8100, 1, [1], recovery.Token);
                    await client.FeatureAsync(0x8100, 3, [0, 1], recovery.Token);
                }
            }
            finally
            {
                try
                {
                    if (notificationScope is not null) await notificationScope.DisposeAsync();
                }
                finally
                {
                    if (notificationScope is not null)
                        await File.WriteAllLinesAsync(Path.Combine(folder, "receiver-notifications-trace.log"), notificationScope.Trace);
                    if (touched)
                    {
                        try
                        {
                            if (originalConfigurationCookie is not null)
                            {
                                await client.FeatureAsync(0x0020, 1, originalConfigurationCookie);
                                await Save("configuration-completion-restored.json", new { Cookie=Convert.ToHexString((await client.FeatureAsync(0x0020, 0))[..2]) });
                            }
                        }
                        finally
                        {
                            var current = await memory.ReadAsync();
                            var restore = MemoryWritePlan.Restore(current, before, true);
                            if (restore.Changes.Count != 0) await memory.CommitAsync(restore, Path.Combine(folder, "backups"));
                            var after = await memory.ReadAsync();
                            await OnboardMemory.SaveBackupAsync(after, Path.Combine(folder, "after.json"));
                            var identical = before.Mode == after.Mode && before.ActiveSector == after.ActiveSector && before.Sectors.All(p => p.Value.SequenceEqual(after.Sectors[p.Key]));
                            await Save("restoration.json", new { AllSectorsModeAndSlotIdentical = identical, PrimaryBindingsChangedDuringProbe = activeKeyboardPresence, PrimaryBindingsRestored = identical });
                            if (!identical) throw new IOException("测试后的全量恢复检查未通过。");
                        }
                    }
                    await File.WriteAllLinesAsync(Path.Combine(folder, "trace.log"), trace);
                }
            }
        }
        Console.WriteLine($"{keyName} 固件对照结束，原配置全量恢复。执行应答与真实输入分别记录。");
        Task Save(string name, object value) => File.WriteAllTextAsync(Path.Combine(folder, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static uint IdleMilliseconds()
    {
        var value = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref value)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return unchecked((uint)Environment.TickCount - value.Time);
    }
    private static bool AnyInputHeld() => Enumerable.Range(1, 254).Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Time; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetLastInputInfo(ref LastInput value);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
