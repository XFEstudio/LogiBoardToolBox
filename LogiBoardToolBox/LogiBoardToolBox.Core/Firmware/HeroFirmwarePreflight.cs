using System.Buffers.Binary;
using System.Text;
using LogiBoardToolBox.Core.Diagnostics;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Core.Windows;

namespace LogiBoardToolBox.Core.Firmware;

public sealed record HeroFeature(byte Index, string Id, byte Type, byte Version, string Raw);
public sealed record HeroFirmwareEntity(byte Index, byte Type, string Prefix, string Version, string Raw);
public sealed class HeroPreflightReport
{
    public string Format { get; } = "LogiBoardToolBox.HeroPreflight.v1";
    public DateTimeOffset At { get; } = DateTimeOffset.Now;
    public string Purpose => DfuImageTransferRequested ? "DFU image-transfer transport verification; not a completed Lua firmware fix."
        : DfuCycleReturnOnly ? "Return to existing firmware using a saved baseline; no image transfer."
        : DfuCycleRequested ? "Upgrade-mode round-trip only; no image transfer; not a Lua firmware installation."
        : "Read-only installation preflight; not an installer or a completed Lua firmware fix.";
    public HidDeviceInfo? Target { get; set; }
    public string Name { get; set; } = "";
    public string Identity { get; set; } = "";
    public string DeviceInfoRaw { get; set; } = "";
    public string BoardDescriptionRaw { get; set; } = "";
    public List<HeroFeature> Features { get; } = [];
    public List<HeroFirmwareEntity> Firmware { get; } = [];
    public Dictionary<string, byte> UpdateFeatureIndices { get; } = [];
    public string? SignedDfuStatusRaw { get; set; }
    public bool? SignedDfuDeclaredAvailable { get; set; }
    public bool KnownApplicationVersionMetadata { get; set; }
    // The normal HID++ interface does not dump the application's executable flash.
    public bool ApplicationImageHashVerified { get; } = false;
    public bool BootloaderImageBackedUp { get; } = false;
    public bool InstallationVerified { get; } = false;
    public bool ExactLuaHardwareVerified { get; } = false;
    public bool HardwareMutationRequestsSent => DfuCycleStartAttempted || DfuCycleRestartAttempted || DfuImageDataMayHaveBeenSent;
    public bool InputGenerated { get; } = false;
    public int AllowedReadRequests { get; set; }
    public int BlockedRequests { get; set; }
    public MemorySnapshot? Before { get; set; }
    public MemorySnapshot? After { get; set; }
    public bool? CompleteBoardBackup { get; set; }
    public bool? BoardStateUnchanged { get; set; }
    public List<string> Differences { get; } = [];
    public List<string> Issues { get; } = [];
    public bool DfuCycleRequested { get; set; }
    public bool DfuCycleReturnOnly { get; set; }
    public string? DfuCycleBaselineFile { get; set; }
    public bool DfuCycleStartAttempted { get; set; }
    public bool DfuCycleRestartAttempted { get; set; }
    public bool DfuCycleBootloaderObserved { get; set; }
    public bool DfuCycleVerified { get; set; }
    public List<string> DfuCycleEvents { get; } = [];
    public MemorySnapshot? DfuCycleReturnedSnapshot { get; set; }
    public List<HeroFirmwareEntity> DfuCycleReturnedFirmware { get; } = [];
    public List<HeroFeature> DfuCycleBootloaderFeatures { get; } = [];
    public List<HeroFirmwareEntity> DfuCycleBootloaderFirmware { get; } = [];
    public string? DfuCycleBootloaderDeviceInfoRaw { get; set; }
    public bool DfuImageTransferRequested { get; set; }
    public bool DfuImageDataMayHaveBeenSent { get; set; }
}

public static class HeroFirmwarePreflight
{
    public const ushort ProductId = 0xC08B;
    public const ushort BootloaderProductId = 0xAAE6;

    /// <summary>Never falls back to a receiver or an arbitrary Logitech mouse.</summary>
    public static HidDeviceInfo SelectTarget(IEnumerable<HidDeviceInfo> inventory)
    {
        var groups = inventory.Where(h => h.ProductId == ProductId && h.OutputReportLength >= 20)
            .GroupBy(h => h.CollectionGroup).ToArray();
        if (groups.Length != 1) throw new InvalidOperationException(groups.Length == 0
            ? "没有找到 USB 046D:C08B 的 G502 HERO；未打开接收器或其他鼠标。"
            : "检测到多个 G502 HERO，请只保留待测的这一只后重试；未选择任何设备。");
        return groups[0].OrderBy(h => h.OutputReportLength).ThenBy(h => h.Path, StringComparer.OrdinalIgnoreCase).First();
    }

    /// <summary>Caller supplies only a C08B vendor collection. All requests pass the read-only boundary.</summary>
    public static async Task CollectAsync(HeroPreflightReport report, HidDeviceInfo target, IHidTransport transport,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        using var readOnly = new HeroReadOnlyTransport(transport);
        if (target.ProductId != ProductId || target.OutputReportLength < 20)
            throw new ArgumentException("只支持有线 046D:C08B G502 HERO 的只读检测。", nameof(target));
        report.Target = target;
        using var client = new HidPlusPlusClient(readOnly, 0xFF);
        try
        {
            progress?.Invoke("正在读取设备标识和固件实体…");
            if (!await client.PingAsync(ct).ConfigureAwait(false)) throw new IOException("设备没有返回有效的 HID++ 版本。");
            report.Name = await client.ReadNameAsync(ct).ConfigureAwait(false);
            report.Identity = $"046D:C08B:{target.SerialNumber}:FF";
            await Stage("firmware", async () =>
            {
                var info = await client.FeatureAsync(0x0003, 0, ct: ct).ConfigureAwait(false);
                report.DeviceInfoRaw = Convert.ToHexString(info);
                report.Identity = $"046D:C08B:{Convert.ToHexString(info, 1, 4)}:FF";
                if (info[0] > 16) throw new IOException("固件实体数量异常；未截断为成功结果。");
                for (byte i = 0; i < info[0]; i++)
                {
                    var e = await client.FeatureAsync(0x0003, 1, [i], ct).ConfigureAwait(false);
                    var prefix = Encoding.ASCII.GetString(e, 1, 3).Trim('\0', ' ');
                    report.Firmware.Add(new(i, (byte)(e[0] & 15), prefix,
                        $"{e[4]:X2}.{e[5]:X2}.B{BinaryPrimitives.ReadUInt16BigEndian(e.AsSpan(6, 2)):X4}", Convert.ToHexString(e)));
                }
                report.KnownApplicationVersionMetadata = report.Firmware.Any(e => e.Type == 0 && e.Prefix == "U1" && e.Version == "27.03.B0010");
            });
            await Stage("feature-set", async () =>
            {
                var count = (await client.FeatureAsync(0x0001, 0, ct: ct).ConfigureAwait(false))[0];
                if (count > 64) throw new IOException("功能数量超出本工具的读取范围。");
                for (byte i = 0; i <= count; i++)
                {
                    var f = await client.FeatureAsync(0x0001, 1, [i], ct).ConfigureAwait(false);
                    report.Features.Add(new(i, $"{f[0]:X2}{f[1]:X2}", f[2], f[3], Convert.ToHexString(f)));
                }
            });
            progress?.Invoke("正在只读查询升级接口状态，不进入升级模式…");
            foreach (ushort feature in new ushort[] { 0x00C1, 0x00C2, 0x00C3, 0x00D0 })
                await Stage($"feature-{feature:X4}", async () =>
                { report.UpdateFeatureIndices[$"{feature:X4}"] = await client.GetFeatureAsync(feature, ct).ConfigureAwait(false); });
            if (report.UpdateFeatureIndices.GetValueOrDefault("00C2") != 0)
                await Stage("dfu-status", async () =>
                {
                    var status = await client.FeatureAsync(0x00C2, 0, ct: ct).ConfigureAwait(false);
                    report.SignedDfuStatusRaw = Convert.ToHexString(status);
                    report.SignedDfuDeclaredAvailable = (status[2] & 1) == 0;
                });

            await Stage("board-backup", async () =>
            {
                var description = await client.FeatureAsync(0x8100, 0, ct: ct).ConfigureAwait(false);
                report.BoardDescriptionRaw = Convert.ToHexString(description);
                var c = OnboardCapabilities.Parse(description);
                if (c.MemoryModel != 1 || c.ProfileFormat != 2 || c.MacroFormat != 1 || c.ProfileCount != 5 ||
                    c.FactoryProfileCount != 5 || c.ButtonCount != 11 || c.SectorCount != 16 || c.SectorSize != 256)
                    throw new IOException("板载布局与待分析的 HERO 不同，停止扇区读取并保留原始能力回复。");
                var memory = new OnboardMemory(client, report.Identity, report.Name);
                progress?.Invoke("正在保存第一份板载快照（用户配置和出厂配置）…");
                report.Before = await memory.ReadAsync(ct: ct).ConfigureAwait(false);
                progress?.Invoke("正在独立重读并核对板载内容…");
                report.After = await memory.ReadAsync(ct: ct).ConfigureAwait(false);
                report.CompleteBoardBackup = Complete(report.Before) && Complete(report.After);
                Compare(report);
            });
        }
        finally
        {
            report.AllowedReadRequests = readOnly.AllowedRequests;
            report.BlockedRequests = readOnly.BlockedRequests;
        }

        async Task Stage(string name, Func<Task> action)
        {
            ct.ThrowIfCancellationRequested();
            try { await action().ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or NotSupportedException)
            { report.Issues.Add($"{name}: {error.Message}"); DiagnosticLog.Record("hero-preflight", name, error: error); }
        }
    }

    private static bool Complete(MemorySnapshot s) => s.ReadErrors.Count == 0 && s.Sectors.Count == 22 &&
        Enumerable.Range(0, 16).Concat(Enumerable.Range(0x100, 6)).All(i => s.Sectors.TryGetValue(i, out var b) && b.Length == 256);

    private static void Compare(HeroPreflightReport report)
    {
        var a = report.Before!; var b = report.After!;
        if (a.Mode != b.Mode) report.Differences.Add("Mode");
        if (a.ActiveSector != b.ActiveSector) report.Differences.Add("ActiveSector");
        if (!a.Description.SequenceEqual(b.Description)) report.Differences.Add("Description");
        foreach (var i in a.Sectors.Keys.Union(b.Sectors.Keys).Order())
            if (!a.Sectors.TryGetValue(i, out var x) || !b.Sectors.TryGetValue(i, out var y) || !x.SequenceEqual(y))
                report.Differences.Add($"Sector 0x{i:X4}");
        report.BoardStateUnchanged = report.CompleteBoardBackup == true && report.Differences.Count == 0;
        if (report.CompleteBoardBackup != true) report.Issues.Add("板载读取不完整；不能将本包作为完整配置备份。");
        if (report.Differences.Count > 0) report.Issues.Add("两次读取期间设备状态发生变化；本工具没有发送修改命令，请保留差异以便分析并排除其他配置程序。");
    }
}
