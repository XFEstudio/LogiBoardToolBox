using LogiBoardToolBox.Core.HidPlusPlus;

namespace LogiBoardToolBox.Core.Firmware;

/// <summary>
/// Restricts one DFU connection to the exact supplied container, in order.
/// This is a byte/command boundary, not signature authentication. The caller
/// must establish image trust and target identity before constructing it.
/// Restart and all other hardware writes are prohibited on this connection.
/// </summary>
public sealed class HeroDfuImageTransport(IHidTransport inner, HeroDfuImage image,
    Func<Task> beforeFirstPacket) : IHidTransport
{
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly object sync = new();
    private byte? queryAddress, dfuIndex;
    private bool failed;
    public int AttemptedPackets { get; private set; }
    public int InputReportLength => inner.InputReportLength;
    public int OutputReportLength => inner.OutputReportLength;

    public async Task WriteAsync(byte[] report, CancellationToken ct)
    {
        await writes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var r = report.ToArray();
            bool first = false;
            lock (sync)
            {
                if (failed || r.Length != OutputReportLength || r.Length < 20 || r[0] != 0x11 || r[1] != 0xFF || (r[3] & 15) == 0)
                    throw new InvalidOperationException("镜像连接拒绝不匹配的设备、报文或失败后的继续传输。");
                var function = r[3] >> 4;
                if (r[2] == 0 && function == 0 && AttemptedPackets == 0 && r[4] == 0 && r[5] == 0xD0 &&
                    r.AsSpan(6).IndexOfAnyExcept((byte)0) < 0)
                    queryAddress = r[3];
                else
                {
                    if (dfuIndex is null || r[2] != dfuIndex || AttemptedPackets >= image.PacketCount ||
                        function != HeroDfuImage.PacketFunction(AttemptedPackets) ||
                        !r.AsSpan(4, 16).SequenceEqual(image.CopyPacket(AttemptedPackets)) ||
                        r.AsSpan(20).IndexOfAnyExcept((byte)0) >= 0)
                        throw new InvalidOperationException("镜像连接只允许选定容器的下一包；禁止复位、越界、重发或替换字节。");
                    first = AttemptedPackets == 0;
                    // Once a write is attempted its delivery may be ambiguous.
                    // Never reuse this connection to replay the same packet.
                    AttemptedPackets++;
                }
            }
            if (first) await beforeFirstPacket().ConfigureAwait(false);
            await inner.WriteAsync(r, ct).ConfigureAwait(false);
        }
        catch { lock (sync) failed = true; throw; }
        finally { writes.Release(); }
    }

    public async Task<byte[]> ReadAsync(CancellationToken ct)
    {
        var r = await inner.ReadAsync(ct).ConfigureAwait(false);
        lock (sync)
        {
            if (queryAddress is { } address && r.Length >= 7 &&
                (r[0] == 0x10 || r[0] == 0x11 && r.Length >= 20 || r[0] == 0x12 && r.Length >= 64) &&
                r[1] == 0xFF && r[2] == 0 && r[3] == address)
            {
                queryAddress = null;
                if (r[4] == 0) return r;
                if (r[4] != 4) throw new IOException("AAE6 的 00D0 索引不是已实测的 04，未传输镜像。");
                dfuIndex = r[4];
            }
        }
        return r;
    }

    public void Dispose() => inner.Dispose();
}
