using LogiBoardToolBox.Core.HidPlusPlus;

namespace LogiBoardToolBox.Core.Firmware;

/// <summary>
/// Narrow protocol boundary for the wired C08B preflight. "WriteAsync" sends
/// HID++ queries; mutating HID++ functions never reach the wrapped transport.
/// Feature indices are learned only from replies to matching root queries.
/// </summary>
public sealed class HeroReadOnlyTransport(IHidTransport inner) : IHidTransport
{
    private readonly object gate = new();
    private readonly Dictionary<byte, ushort> features = new() { [0] = 0 };
    private static readonly HashSet<ushort> QueryFeatures = [0x0001, 0x0003, 0x0005, 0x00C1, 0x00C2, 0x00C3, 0x00D0, 0x8100];
    private (byte Address, ushort Feature)? rootQuery;
    public int InputReportLength => inner.InputReportLength;
    public int OutputReportLength => inner.OutputReportLength;
    public int AllowedRequests { get; private set; }
    public int BlockedRequests { get; private set; }

    public Task WriteAsync(byte[] report, CancellationToken cancellationToken)
    {
        var checkedReport = report.ToArray();
        lock (gate)
        {
            if (!Allowed(checkedReport))
            {
                BlockedRequests++;
                throw new InvalidOperationException("HERO 只读检测拒绝了未在白名单内的 HID++ 请求。");
            }
            rootQuery = checkedReport[2] == 0 && (checkedReport[3] >> 4) == 0
                ? (checkedReport[3], (ushort)((checkedReport[4] << 8) | checkedReport[5])) : null;
            AllowedRequests++;
        }
        return inner.WriteAsync(checkedReport, cancellationToken);
    }

    private bool Allowed(byte[] r)
    {
        if (r.Length != OutputReportLength || r.Length < 20 || r[0] != 0x11 || r[1] != 0xFF ||
            (r[3] & 15) == 0 || !features.TryGetValue(r[2], out var feature)) return false;
        var fn = r[3] >> 4;
        var p = r.AsSpan(4);
        bool Zeros(int from) => r.AsSpan(4 + from).IndexOfAnyExcept((byte)0) < 0;
        return feature switch
        {
            0 => fn == 0 && QueryFeatures.Contains((ushort)((p[0] << 8) | p[1])) && Zeros(2) ||
                 fn == 1 && p[0] == 0 && p[1] == 0 && p[2] != 0 && Zeros(3),
            0x0001 => fn == 0 && Zeros(0) || fn == 1 && p[0] <= 64 && Zeros(1),
            0x0003 => fn == 0 && Zeros(0) || fn == 1 && p[0] < 16 && Zeros(1),
            0x0005 => fn == 0 && Zeros(0) || fn == 1 && Zeros(1),
            0x00C2 => fn == 0 && Zeros(0), // getDfuStatus, never startDfu
            0x8100 => fn is 0 or 2 or 4 && Zeros(0) || fn == 5 &&
                ((p[0] == 0 && p[1] < 16) || (p[0] == 1 && p[1] <= 5)) &&
                p[2] == 0 && p[3] <= 240 && p[3] % 16 == 0 && Zeros(4),
            _ => false // C1/C3/D0 presence may be queried; no commands to them.
        };
    }

    public async Task<byte[]> ReadAsync(CancellationToken cancellationToken)
    {
        var r = await inner.ReadAsync(cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            if (rootQuery is { } pending && r.Length >= 7 &&
                (r[0] == 0x10 || r[0] == 0x11 && r.Length >= 20 || r[0] == 0x12 && r.Length >= 64) &&
                r[1] == 0xFF && r[2] == 0 && r[3] == pending.Address)
            {
                rootQuery = null;
                var index = r[4];
                if (index is > 0 and < 0xFF)
                {
                    if (features.TryGetValue(index, out var old) && old != pending.Feature)
                        throw new IOException("功能索引冲突；停止 HERO 只读检测。");
                    features[index] = pending.Feature;
                }
            }
        }
        return r;
    }

    public void Dispose() => inner.Dispose();
}
