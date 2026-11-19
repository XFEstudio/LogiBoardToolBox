using LogiBoardToolBox.Core.HidPlusPlus;

namespace LogiBoardToolBox.Core.Firmware;

public enum HeroDfuControlRole { Application, Bootloader }

/// <summary>
/// Upgrade-mode round-trip boundary. Allows only root/firmware reads plus the
/// exact C2 startDfu or D0 restart request for its role. NEVER allows D0 data or
/// start-image functions, board writes, or unknown commands. Separate from the
/// unconditionally read-only preflight transport.
/// </summary>
public sealed class HeroDfuControlTransport(IHidTransport inner, HeroDfuControlRole role, byte deviceIndex) : IHidTransport
{
    private readonly Dictionary<byte, ushort> indices = new() { [0] = 0 };
    private readonly object gate = new();
    private (byte Address, ushort Feature)? pending;
    public int InputReportLength => inner.InputReportLength;
    public int OutputReportLength => inner.OutputReportLength;
    public int ControlRequests { get; private set; }

    public Task WriteAsync(byte[] report, CancellationToken ct)
    {
        var r = report.ToArray();
        lock (gate)
        {
            if (!Enum.IsDefined(role) || deviceIndex is not (0 or 0xFF) || r.Length != OutputReportLength || r.Length < 20 || r[0] != 0x11 || r[1] != deviceIndex || (r[3] & 15) == 0 ||
                !indices.TryGetValue(r[2], out var feature)) throw new InvalidOperationException("升级模式测试拒绝未声明的请求。");
            var fn = r[3] >> 4;
            bool Zeros(int offset) => r.AsSpan(4 + offset).IndexOfAnyExcept((byte)0) < 0;
            var wanted = role == HeroDfuControlRole.Application ? (ushort)0x00C2 : (ushort)0x00D0;
            var requested = (ushort)((r[4] << 8) | r[5]);
            var query = feature == 0 && (fn == 0 && (requested == wanted || requested == 3 || requested == 1) && Zeros(2) ||
                fn == 1 && r[4] == 0 && r[5] == 0 && r[6] != 0 && Zeros(3)) ||
                feature == 3 && (fn == 0 && Zeros(0) || fn == 1 && r[4] < 16 && Zeros(1)) ||
                feature == 1 && (fn == 0 && Zeros(0) || fn == 1 && r[4] <= 64 && Zeros(1)) ||
                feature == 0x00C2 && role == HeroDfuControlRole.Application && fn == 0 && Zeros(0);
            var control = feature == wanted && (role == HeroDfuControlRole.Application
                ? fn == 1 && r.AsSpan(4, 7).SequenceEqual(new byte[] { 1, 0, 0, 0, 68, 70, 85 }) && Zeros(7)
                : fn == 5 && Zeros(0));
            if (!query && !control) throw new InvalidOperationException("升级模式测试禁止镜像传输、擦除、配置修改或未知命令。");
            if (control) ControlRequests++;
            pending = feature == 0 && fn == 0 ? (r[3], requested) : null;
        }
        return inner.WriteAsync(r, ct);
    }

    public async Task<byte[]> ReadAsync(CancellationToken ct)
    {
        var r = await inner.ReadAsync(ct).ConfigureAwait(false);
        lock (gate)
        {
            if (pending is { } q && r.Length >= 7 && (r[0] == 0x10 || r[0] == 0x11 && r.Length >= 20 || r[0] == 0x12 && r.Length >= 64) &&
                r[1] == deviceIndex && r[2] == 0 && r[3] == q.Address)
            {
                pending = null;
                if (r[4] is > 0 and < 255)
                {
                    if (indices.TryGetValue(r[4], out var previous) && previous != q.Feature) throw new IOException("升级接口索引冲突。");
                    indices[r[4]] = q.Feature;
                }
            }
        }
        return r;
    }
    public void Dispose() => inner.Dispose();
}
