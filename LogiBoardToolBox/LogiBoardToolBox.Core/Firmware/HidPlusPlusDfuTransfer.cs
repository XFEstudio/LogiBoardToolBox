using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LogiBoardToolBox.Core.Diagnostics;
using LogiBoardToolBox.Core.HidPlusPlus;

namespace LogiBoardToolBox.Core.Firmware;

public enum DfuStatusKind { Success, Busy, Failure }

public sealed record DfuStatus(uint DeviceCounter, byte RawStatus)
{
    public byte Code => (byte)(RawStatus & 0x7F);
    public DfuStatusKind Kind => Code switch
    {
        0x01 or 0x02 or 0x05 or 0x06 => DfuStatusKind.Success,
        0x03 or 0x19 or 0x23 => DfuStatusKind.Busy,
        _ => DfuStatusKind.Failure
    };

    public static DfuStatus Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 5) throw new InvalidDataException("DFU 状态回复不足 5 字节。");
        return new(BinaryPrimitives.ReadUInt32BigEndian(payload[..4]), payload[4]);
    }
}

public sealed class DfuTransferException(int packetIndex, string message, DfuStatus? status = null, Exception? inner = null)
    : IOException($"DFU 第 {packetIndex + 1} 包：{message}", inner)
{
    public int PacketIndex { get; } = packetIndex;
    public DfuStatus? Status { get; } = status;
}

public sealed record DfuPacketProgress(int AcknowledgedPackets, int TotalPackets, DfuStatus Status);

public sealed record DfuTransferResult(int AcknowledgedPackets, DfuStatus LastStatus)
{
    // Acknowledged stream != authenticated, installed, booted, or physically tested.
    public bool InstallationVerified => false;
    public bool RestartRequested => false;
}

/// <summary>
/// Protocol layer for a caller-selected client already in HID++ 00D0 mode.
/// It does not select/open hardware, enter DFU, reset, sign, or authenticate an
/// image. Those are separate installation responsibilities. No product UI or
/// preflight executable invokes this transport in its round-trip mode.
///
/// Packet window and status classes are independently implemented from the
/// public fwupd logitech-hidpp protocol. Device counters are diagnostic data,
/// not an invented request ID: the public implementation does not require
/// equality between a busy reply and the subsequent completion notification.
/// Only one packet is outstanding, on the selected device and feature.
/// </summary>
public static class HidPlusPlusDfuTransfer
{
    private const ushort DfuFeature = 0x00D0;
    private static readonly ConditionalWeakTable<HidPlusPlusClient, SemaphoreSlim> Gates = new();

    public static async Task<DfuTransferResult> SendAsync(HidPlusPlusClient client, HeroDfuImage image,
        IProgress<DfuPacketProgress>? progress = null, TimeSpan? eventTimeout = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(image);
        var timeout = eventTimeout ?? TimeSpan.FromSeconds(15);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(60)) throw new ArgumentOutOfRangeException(nameof(eventTimeout));
        var gate = Gates.GetValue(client, _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = await client.GetFeatureAsync(DfuFeature, ct).ConfigureAwait(false);
            if (index == 0) throw new NotSupportedException("设备当前没有 00D0 升级接口；未进入升级模式或发送镜像。");
            var notices = Channel.CreateBounded<HidNotification>(new BoundedChannelOptions(64)
            { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
            Exception? notificationFailure = null;
            client.Notification += OnNotification;
            client.Disconnected += OnDisconnected;
            try
            {
                DfuStatus? last = null;
                for (var packet = 0; packet < image.PacketCount; packet++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref notificationFailure) is { } receiveError)
                        throw new DfuTransferException(packet, receiveError.Message, inner: receiveError);
                    // No packet is sent until the preceding one has completed.
                    // Discard notices already queued before this request.
                    while (notices.Reader.TryRead(out _)) { }
                    try
                    {
                        var reply = await client.CallAsync(index, HeroDfuImage.PacketFunction(packet), image.CopyPacket(packet), ct).ConfigureAwait(false);
                        var status = DfuStatus.Parse(reply);
                        if (Volatile.Read(ref notificationFailure) is { } replyError) throw new IOException(replyError.Message, replyError);
                        Record(packet, "reply", status);
                        if (status.Kind == DfuStatusKind.Busy)
                        {
                            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            deadline.CancelAfter(timeout);
                            do
                            {
                                HidNotification notice;
                                try { notice = await notices.Reader.ReadAsync(deadline.Token).ConfigureAwait(false); }
                                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                                { throw new TimeoutException("等待 DFU 完成通知超时；没有重发状态性数据包。"); }
                                status = DfuStatus.Parse(notice.Data);
                                Record(packet, "notification", status);
                            } while (status.Kind == DfuStatusKind.Busy);
                        }
                        if (status.Kind != DfuStatusKind.Success)
                            throw new DfuTransferException(packet, $"设备返回状态 0x{status.RawStatus:X2}，已停止传输。", status);
                        last = status;
                        progress?.Report(new(packet + 1, image.PacketCount, status));
                    }
                    catch (DfuTransferException) { throw; }
                    catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or ChannelClosedException)
                    { throw new DfuTransferException(packet, error.Message, inner: error); }
                }
                return new(image.PacketCount, last!);
            }
            finally
            {
                client.Notification -= OnNotification;
                client.Disconnected -= OnDisconnected;
                notices.Writer.TryComplete();
            }

            void OnNotification(object? sender, HidNotification notice)
            {
                if (notice.FeatureIndex != index) return;
                if (!notices.Writer.TryWrite(notice))
                {
                    var error = new IOException("DFU 通知队列溢出；无法确认传输状态。");
                    Interlocked.CompareExchange(ref notificationFailure, error, null);
                    notices.Writer.TryComplete(error);
                }
            }
            void OnDisconnected(object? sender, Exception error)
            {
                Interlocked.CompareExchange(ref notificationFailure, error, null);
                notices.Writer.TryComplete(error);
            }
            void Record(int packet, string stage, DfuStatus status) => DiagnosticLog.Record("dfu-transfer", stage,
                new { client.DiagnosticId, PacketIndex = packet, status.DeviceCounter, status.RawStatus, Kind = status.Kind.ToString() });
        }
        finally { gate.Release(); }
    }
}
