using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Windows;

namespace LogiBoardToolBox.Core.Firmware;

public enum HeroDfuPhase
{
    Prepared, EnteringDfu, BootloaderValidated, TransferMayHaveStarted,
    TransferAcknowledged, RestartMayHaveBeenSent, ApplicationVerified, Failed
}

public sealed class HeroDfuJournal
{
    public string Format { get; } = "LogiBoardToolBox.HeroDfuJournal.v1";
    public DateTimeOffset At { get; } = DateTimeOffset.Now;
    public HeroDfuPhase Phase { get; set; }
    public HeroDfuPhase? FailedFrom { get; set; }
    public string ImageSha256 { get; set; } = "";
    public bool OfficialReference { get; set; }
    public int AcknowledgedPackets { get; set; }
    public byte? LastDeviceStatus { get; set; }
    public bool MustReflashBeforeRestart { get; set; }
    public bool ApplicationReturnVerified { get; set; }
    public bool ExecutableFlashReadBack { get; } = false;
    public bool CustomFirmwareInstalled { get; } = false;
    public bool ExactLuaHardwareVerified { get; } = false;
    public string? Error { get; set; }
    public HeroPreflightReport Baseline { get; set; } = new();
}

/// <summary>
/// Installation transport transaction, separate from read-only/round-trip
/// tools. The outer application establishes image trust; this layer enforces
/// the observed target, a durable write-ahead record, exact packet content,
/// terminal device status, and full settings comparison after reboot.
/// A failed/ambiguous transfer never causes an automatic restart or replay.
/// </summary>
public sealed class HeroDfuTransaction(Func<IReadOnlyList<HidDeviceInfo>> inventory,
    Func<HidDeviceInfo, IHidTransport> open, Func<HeroDfuJournal, Task> persist,
    Action<string>? progress = null, TimeSpan? transitionTimeout = null)
{
    public async Task RunAsync(HeroDfuJournal journal, HeroDfuImage image, CancellationToken ct)
    {
        if (journal.Phase != HeroDfuPhase.Prepared)
            throw new InvalidOperationException("已有传输状态，必须使用明确的官方镜像恢复流程。");
        ValidateImage(journal, image);
        var cycle = new HeroDfuRoundTrip(inventory, open, progress, transitionTimeout);
        try
        {
            await Save(HeroDfuPhase.EnteringDfu);
            var boot = await cycle.EnterAsync(journal.Baseline, ct);
            ValidateBootloader(journal.Baseline);
            await TransferAndReturn(boot, journal, image, cycle, ct);
        }
        catch (Exception error) { await Failed(error); throw; }

        async Task Save(HeroDfuPhase phase) { journal.Phase = phase; await persist(journal); }
        async Task Failed(Exception error) => await RecordFailure(journal, error);
    }

    /// <summary>
    /// Starts a NEW complete stream from packet zero on the same observed unit.
    /// It is not a retry of an ambiguous packet. The official-reference CLI is
    /// the only shipped caller and only supplies the hash-pinned vendor image.
    /// The device's ability to recover an interrupted flash is not established
    /// by a mode-only round-trip or by these host-side checks.
    /// </summary>
    public async Task RestoreReferenceAsync(HeroDfuJournal journal, HeroDfuImage officialImage, CancellationToken ct)
    {
        if (!officialImage.MatchesOfficialReference) throw new InvalidDataException("恢复只接受固定 SHA-256 的官方原始镜像。");
        journal.ApplicationReturnVerified = false; journal.Baseline.DfuCycleVerified = false;
        journal.AcknowledgedPackets = 0; journal.LastDeviceStatus = null;
        if (journal.Baseline.Before is null || journal.Baseline.CompleteBoardBackup != true ||
            !journal.Baseline.KnownApplicationVersionMetadata)
            throw new InvalidDataException("没有完整的原始 HERO 基线，未发送恢复命令。");
        var current = inventory();
        if (current.Any(h => h.ProductId == HeroFirmwarePreflight.ProductId))
            throw new InvalidOperationException("正常 HERO 已在线，不自动重新进入升级模式；请保留报告。");
        var groups = current.Where(h => h.ProductId == HeroFirmwarePreflight.BootloaderProductId && h.OutputReportLength >= 20)
            .GroupBy(h => h.CollectionGroup).ToArray();
        if (groups.Length != 1) throw new InvalidOperationException("没有唯一的 AAE6 升级接口，未发送恢复数据。");
        var boot = groups[0].OrderBy(h => h.OutputReportLength).First();
        var cycle = new HeroDfuRoundTrip(inventory, open, progress, transitionTimeout);
        try
        {
            journal.Baseline.DfuCycleBootloaderFeatures.Clear();
            journal.Baseline.DfuCycleBootloaderFirmware.Clear();
            journal.Baseline.DfuCycleBootloaderDeviceInfoRaw = null;
            await cycle.ReadBootInventoryAsync(boot, journal.Baseline, ct);
            ValidateBootloader(journal.Baseline);
            ValidateImage(journal, officialImage);
            journal.ApplicationReturnVerified = false; journal.Error = null; journal.FailedFrom = null;
            await TransferAndReturn(boot, journal, officialImage, cycle, ct);
        }
        catch (Exception error) { await RecordFailure(journal, error); throw; }
    }

    private async Task TransferAndReturn(HidDeviceInfo boot, HeroDfuJournal journal, HeroDfuImage image,
        HeroDfuRoundTrip cycle, CancellationToken ct)
    {
        await Save(HeroDfuPhase.BootloaderValidated);
        progress?.Invoke($"开始传输 {image.PacketCount} 包。此阶段若中断，不会自动启动不完整的应用。");
        using (var boundary = new HeroDfuImageTransport(open(boot), image, async () =>
        {
            journal.MustReflashBeforeRestart = true;
            journal.Baseline.DfuImageDataMayHaveBeenSent = true;
            await Save(HeroDfuPhase.TransferMayHaveStarted);
        }))
        using (var client = new HidPlusPlusClient(boundary, 0xFF))
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            var result = await HidPlusPlusDfuTransfer.SendAsync(client, image,
                new InlineProgress(p =>
                {
                    journal.AcknowledgedPackets = p.AcknowledgedPackets;
                    journal.LastDeviceStatus = p.Status.RawStatus;
                    if (p.AcknowledgedPackets % 128 == 0 || p.AcknowledgedPackets == p.TotalPackets)
                        progress?.Invoke($"镜像传输 {p.AcknowledgedPackets}/{p.TotalPackets}");
                }), ct: ct);
            // A generic packet ACK is not the terminal DFU completion status.
            if (result.AcknowledgedPackets != image.PacketCount || result.LastStatus.Code is not (2 or 5 or 6))
                throw new IOException("未收到整个 DFU 的完成状态；保留升级模式，不请求启动应用。");
        }
        journal.MustReflashBeforeRestart = false;
        await Save(HeroDfuPhase.TransferAcknowledged);
        await Save(HeroDfuPhase.RestartMayHaveBeenSent);
        await cycle.ReturnToApplicationAsync(boot, journal.Baseline, ct);
        journal.ApplicationReturnVerified = true;
        await Save(HeroDfuPhase.ApplicationVerified);

        async Task Save(HeroDfuPhase phase) { journal.Phase = phase; await persist(journal); }
    }

    private static void ValidateImage(HeroDfuJournal journal, HeroDfuImage image)
    {
        ArgumentNullException.ThrowIfNull(journal); ArgumentNullException.ThrowIfNull(image);
        journal.ImageSha256 = image.Sha256; journal.OfficialReference = image.MatchesOfficialReference;
        journal.AcknowledgedPackets = 0; journal.LastDeviceStatus = null;
        journal.Baseline.DfuImageTransferRequested = true;
        journal.Baseline.DfuCycleVerified = false;
    }

    private static void ValidateBootloader(HeroPreflightReport report)
    {
        var raw = report.DfuCycleBootloaderDeviceInfoRaw is { } hex ? Convert.FromHexString(hex) : [];
        var before = Convert.FromHexString(report.DeviceInfoRaw);
        if (raw.Length < 9 || before.Length < 9 || raw[0] != 2 || !raw.AsSpan(1, 4).SequenceEqual(before.AsSpan(1, 4)) ||
            raw[7] != 0xAA || raw[8] != 0xE6)
            throw new IOException("Bootloader 单元标识与原鼠标不一致或读取不完整，未发送镜像。");
        string[] expected = ["0000", "0001", "0003", "0005", "00D0"];
        if (!report.DfuCycleBootloaderFeatures.Select(f => f.Id).SequenceEqual(expected) ||
            report.DfuCycleBootloaderFeatures.Where((f, i) => f.Index != i || f.Type != 0).Any() ||
            !report.DfuCycleBootloaderFirmware.Any(f => f.Type == 1 && f.Prefix == "BOT" && f.Version == "81.01.B0003"))
            throw new IOException("不是用户实测的 AAE6 / BOT 81.01.B0003 和五项功能表，未发送镜像。");
    }

    private async Task RecordFailure(HeroDfuJournal journal, Exception error)
    {
        journal.FailedFrom = journal.Phase; journal.Phase = HeroDfuPhase.Failed; journal.Error = error.ToString();
        try { await persist(journal); }
        catch (Exception saveError) { error.Data["JournalSaveFailure"] = saveError.ToString(); }
    }

    private sealed class InlineProgress(Action<DfuPacketProgress> action) : IProgress<DfuPacketProgress>
    { public void Report(DfuPacketProgress value) => action(value); }
}
