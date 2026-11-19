using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Windows;

namespace LogiBoardToolBox.Core.Firmware;

public sealed class HeroDfuRoundTrip(Func<IReadOnlyList<HidDeviceInfo>> inventory, Func<HidDeviceInfo, IHidTransport> open,
    Action<string>? progress = null, TimeSpan? transitionTimeout = null)
{
    public async Task RunAsync(HeroPreflightReport report, CancellationToken ct)
    {
        var boot = await EnterAsync(report, ct);
        await ReturnToApplicationAsync(boot, report, ct);
    }

    // Shared with the separate image-transfer transaction. This method only
    // enters DFU and reads inventory; it never programs or restarts an image.
    internal async Task<HidDeviceInfo> EnterAsync(HeroPreflightReport report, CancellationToken ct)
    {
        if (transitionTimeout is { } interval && (interval <= TimeSpan.Zero || interval > TimeSpan.FromSeconds(60)))
            throw new ArgumentOutOfRangeException(nameof(transitionTimeout));
        if (report.Target is not { } target || !report.KnownApplicationVersionMetadata || report.CompleteBoardBackup != true || report.BoardStateUnchanged != true || report.Issues.Count != 0)
            throw new InvalidOperationException("原始固件、布局或两份备份未核对通过，未请求升级模式。");
        if (target.ProductId != HeroFirmwarePreflight.ProductId || target.OutputReportLength < 20)
            throw new InvalidOperationException("目标不是 C08B 的厂商长报文接口。");
        if (!report.Firmware.Any(e => e.Prefix == "BOT" && e.Version == "81.00.B0003"))
            throw new InvalidOperationException("不是本次用户报告的 BOT 81.00.B0003，未请求升级模式。");
        if (inventory().Any(h => h.ProductId == HeroFirmwarePreflight.BootloaderProductId))
            throw new InvalidOperationException("已经存在 AAE6 升级接口，不能唯一关联新出现的设备。");
        report.DfuCycleRequested = true;
        progress?.Invoke("即将进入升级模式。请保持 USB 连接。");
        using (var control = new HidPlusPlusClient(new HeroDfuControlTransport(open(target), HeroDfuControlRole.Application, 0xFF), 0xFF))
        {
            var currentInfo = await control.FeatureAsync(0x0003, 0, ct: ct);
            if (Convert.ToHexString(currentInfo) != report.DeviceInfoRaw)
                throw new IOException("进入升级模式前设备标识已改变，未发送 startDfu。");
            if (currentInfo[0] != report.Firmware.Count) throw new IOException("固件实体数量已改变。");
            for (byte i = 0; i < currentInfo[0]; i++)
                if (Convert.ToHexString(await control.FeatureAsync(0x0003, 1, [i], ct)) != report.Firmware[i].Raw)
                    throw new IOException("进入升级模式前固件实体已改变，未发送 startDfu。");
            var index = await control.GetFeatureAsync(0x00C2, ct);
            if (index == 0) throw new IOException("00C2 升级控制接口消失，未请求升级模式。");
            if (index != report.UpdateFeatureIndices.GetValueOrDefault("00C2") || !report.Features.Any(f => f.Id == "00C2" && f.Index == index))
                throw new IOException("升级控制索引与已读取的功能表不一致，未发送 startDfu。");
            var status = await control.CallAsync(index, 0, cancellationToken: ct);
            if ((status[2] & 1) != 0) throw new IOException("设备当前不允许进入升级模式。");
            report.DfuCycleStartAttempted = true;
            try { await control.CallAsync(index, 1, [1, 0, 0, 0, 68, 70, 85], ct); }
            catch (Exception error) when (error is IOException or TimeoutException)
            { report.DfuCycleEvents.Add("startDfu 回复/断连：" + error.Message); }
        }
        var boot = await WaitForAsync(HeroFirmwarePreflight.BootloaderProductId, transitionTimeout ?? TimeSpan.FromSeconds(20), ct);
        report.DfuCycleBootloaderObserved = true;
        report.DfuCycleEvents.Add("观察到唯一的新 AAE6 厂商接口。");
        // Optional reads use a separate connection so their timeout/error cannot
        // poison the connection used for returning to the original application.
        await ReadBootInventoryAsync(boot, report, ct);
        return boot;
    }

    public async Task ReturnOnlyAsync(HeroPreflightReport report, CancellationToken ct)
    {
        if (report.DfuImageDataMayHaveBeenSent)
            throw new InvalidOperationException("已记录镜像传输；仅返回命令不能用作不完整固件的恢复。请使用官方镜像恢复流程。");
        if (transitionTimeout is { } interval && (interval <= TimeSpan.Zero || interval > TimeSpan.FromSeconds(60)))
            throw new ArgumentOutOfRangeException(nameof(transitionTimeout));
        if (report.Target?.ProductId != HeroFirmwarePreflight.ProductId || !report.KnownApplicationVersionMetadata ||
            report.Before is null || report.CompleteBoardBackup != true || report.Before.Sectors.Count != 22 ||
            !report.Firmware.Any(e => e.Prefix == "BOT" && e.Version == "81.00.B0003"))
            throw new InvalidOperationException("没有完整的本次 HERO 基线，未发送任何返回命令。");
        var current = inventory();
        if (current.Any(h => h.ProductId == HeroFirmwarePreflight.ProductId))
            throw new InvalidOperationException("HERO 应用接口已在线，未发送升级模式返回命令。");
        var boots = current.Where(h => h.ProductId == HeroFirmwarePreflight.BootloaderProductId && h.OutputReportLength >= 20)
            .GroupBy(h => h.CollectionGroup).ToArray();
        if (boots.Length != 1) throw new InvalidOperationException("没有唯一 AAE6 升级接口，未发送返回命令。");
        report.DfuCycleReturnOnly = true; report.DfuCycleRequested = true; report.DfuCycleBootloaderObserved = true;
        await ReturnToApplicationAsync(boots[0].OrderBy(h => h.OutputReportLength).First(), report, ct);
    }

    internal async Task ReturnToApplicationAsync(HidDeviceInfo boot, HeroPreflightReport report, CancellationToken ct)
    {
        var restartSent = false;
        foreach (byte address in new byte[] { 0xFF, 0 })
        {
            try
            {
                using var client = new HidPlusPlusClient(new HeroDfuControlTransport(open(boot), HeroDfuControlRole.Bootloader, address), address);
                client.Timeout = TimeSpan.FromSeconds(2);
                var index = await client.GetFeatureAsync(0x00D0, ct);
                if (index == 0) continue;
                var confirm = await client.CallAsync(0, 0, [0, 0xD0], ct);
                var declared = report.DfuCycleBootloaderFeatures.FirstOrDefault(f => f.Id == "00D0");
                if (confirm[0] != index || declared is not null && declared.Index != index)
                    throw new IOException("00D0 索引的独立查询或功能表不一致，未发送 restart。");
                report.DfuCycleEvents.Add($"00D0 索引 {index:X2}，地址 {address:X2}；请求 restart(0)。");
                restartSent = true; report.DfuCycleRestartAttempted = true;
                try { await client.CallAsync(index, 5, [0], ct); }
                catch (Exception error) when (error is IOException or TimeoutException)
                { report.DfuCycleEvents.Add("restart 回复/断连：" + error.Message); }
                break;
            }
            catch (Exception error) when (error is IOException or TimeoutException or System.ComponentModel.Win32Exception)
            { report.DfuCycleEvents.Add($"Bootloader 地址 {address:X2} 查询：{error.Message}"); }
        }
        if (!restartSent) throw new IOException("没有取得 00D0，未发送任何 DFU 数据；当前升级模式返回尚未验证。");
        var returned = await WaitForAsync(HeroFirmwarePreflight.ProductId, transitionTimeout ?? TimeSpan.FromSeconds(20), ct);
        var after = new HeroPreflightReport();
        await HeroFirmwarePreflight.CollectAsync(after, returned, open(returned), progress, ct);
        report.DfuCycleReturnedSnapshot = after.Before;
        report.DfuCycleReturnedFirmware.AddRange(after.Firmware);
        var before = report.Before!; var current = after.Before;
        report.DfuCycleVerified = after.Issues.Count == 0 && after.CompleteBoardBackup == true && after.BoardStateUnchanged == true &&
            after.Identity == report.Identity && after.Firmware.Select(e => e.Raw).SequenceEqual(report.Firmware.Select(e => e.Raw)) &&
            current is not null && before.Mode == current.Mode && before.ActiveSector == current.ActiveSector &&
            before.Description.SequenceEqual(current.Description) && before.Sectors.Count == current.Sectors.Count &&
            before.Sectors.All(p => current.Sectors.TryGetValue(p.Key, out var value) && p.Value.SequenceEqual(value));
        if (!report.DfuCycleVerified) throw new IOException("升级模式返回后的身份、固件或板载内容不匹配；保留全部回复，不能标记返回验证成功。");
        progress?.Invoke("应用返回已核对；版本和全部板载内容保持一致。尚未验证修改固件。");
    }

    private async Task<HidDeviceInfo> WaitForAsync(ushort pid, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var groups = inventory().Where(h => h.ProductId == pid && h.OutputReportLength >= 20)
                .GroupBy(h => h.CollectionGroup).ToArray();
            if (groups.Length > 1) throw new IOException($"出现多个 {pid:X4}，停止自动选择。");
            if (groups.Length == 1) return groups[0].OrderBy(h => h.OutputReportLength).First();
            await Task.Delay(250, ct);
        }
        throw new TimeoutException($"等待 USB 046D:{pid:X4} 超时；请保留报告，当前返回行为未确认。");
    }

    internal async Task ReadBootInventoryAsync(HidDeviceInfo boot, HeroPreflightReport report, CancellationToken ct)
    {
        try
        {
            using var client = new HidPlusPlusClient(new HeroDfuControlTransport(open(boot), HeroDfuControlRole.Bootloader, 0xFF), 0xFF);
            client.Timeout = TimeSpan.FromMilliseconds(750);
            var set = await client.GetFeatureAsync(1, ct);
            if (set != 0)
            {
                var count = (await client.CallAsync(set, 0, cancellationToken: ct))[0];
                if (count > 64) throw new IOException("Bootloader 功能数量异常。");
                for (byte i = 0; i <= count; i++)
                {
                    var f = await client.CallAsync(set, 1, [i], ct);
                    if (f.Length < 3) throw new IOException("Bootloader 功能回复不足 3 字节。");
                    report.DfuCycleBootloaderFeatures.Add(new(i, $"{f[0]:X2}{f[1]:X2}", f[2], f.Length > 3 ? f[3] : (byte)0, Convert.ToHexString(f)));
                }
            }
            var fw = await client.GetFeatureAsync(3, ct);
            if (fw != 0)
            {
                var info = await client.CallAsync(fw, 0, cancellationToken: ct);
                report.DfuCycleBootloaderDeviceInfoRaw = Convert.ToHexString(info);
                var count = info[0];
                if (count > 16) throw new IOException("Bootloader 固件实体数量异常。");
                for (byte i = 0; i < count; i++)
                {
                    var e = await client.CallAsync(fw, 1, [i], ct);
                    if (e.Length < 8) throw new IOException("Bootloader 固件实体回复不足 8 字节。");
                    report.DfuCycleBootloaderFirmware.Add(new(i, (byte)(e[0] & 15), System.Text.Encoding.ASCII.GetString(e, 1, 3).Trim('\0', ' '),
                        $"{e[4]:X2}.{e[5]:X2}.B{System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(e.AsSpan(6, 2)):X4}", Convert.ToHexString(e)));
                }
            }
        }
        catch (Exception error) when (error is IOException or TimeoutException or System.ComponentModel.Win32Exception)
        { report.DfuCycleEvents.Add("Bootloader 可选信息读取未完成：" + error.Message); }
    }
}
