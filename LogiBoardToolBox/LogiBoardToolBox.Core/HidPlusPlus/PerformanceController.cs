using System.Buffers.Binary;

namespace LogiBoardToolBox.Core.HidPlusPlus;

public sealed record SensorPerformance(bool Extended, byte Flags, IReadOnlyList<int> DpiX, IReadOnlyList<int> DpiY,
    ushort CurrentX, ushort CurrentY, ushort DefaultX, ushort DefaultY, byte Lod,
    IReadOnlyList<int> WiredRates, IReadOnlyList<int> WirelessRates, IReadOnlyList<int> ActualRates, int WiredRate, int WirelessRate)
{
    public bool IndependentAxes => (Flags & 1) != 0;
    public int Minimum => DpiX.Min();
    public int Maximum => DpiX.Max();
    public void ValidateDpi(int x, int y)
    {
        if (!DpiX.Contains(x) || IndependentAxes && !DpiY.Contains(y))
            throw new ArgumentOutOfRangeException(nameof(x), $"DPI 必须是传感器支持的数值（{Minimum}–{Maximum}）。");
        if (!IndependentAxes && x != y) throw new NotSupportedException("此传感器不支持独立 X/Y DPI。");
    }
}

public sealed class PerformanceController(HidPlusPlusClient client, bool wireless)
{
    public SensorPerformance? State { get; private set; }
    private bool extendedRate;
    public async Task<SensorPerformance> ReadAsync(CancellationToken ct = default)
    {
        var extended = await client.GetFeatureAsync(0x2202, ct).ConfigureAwait(false) != 0;
        byte flags = 0, lod = 0; ushort x, y, dx, dy;
        IReadOnlyList<int> rangeX, rangeY;
        if (extended)
        {
            var caps = await client.FeatureAsync(0x2202, 1, [0], ct).ConfigureAwait(false);
            Require(caps, 3); flags = caps[2];
            rangeX = await ReadExtendedRangeAsync(0, ct).ConfigureAwait(false);
            rangeY = (flags & 1) != 0 ? await ReadExtendedRangeAsync(1, ct).ConfigureAwait(false) : rangeX;
            var current = await client.FeatureAsync(0x2202, 5, [0], ct).ConfigureAwait(false);
            Require(current, 10); x = Be(current, 1); dx = Be(current, 3); y = Be(current, 5); dy = Be(current, 7); lod = current[9];
            if (x == 0) x = dx; if (y == 0) y = (flags & 1) != 0 ? dy : x;
        }
        else
        {
            var values = await client.FeatureAsync(0x2201, 1, [0], ct).ConfigureAwait(false);
            Require(values, 3); rangeX = DecodeRanges(values.AsSpan(1)); rangeY = rangeX;
            var current = await client.FeatureAsync(0x2201, 2, [0], ct).ConfigureAwait(false);
            Require(current, 5); x = Be(current, 1); dx = Be(current, 3); if (x == 0) x = dx; y = x; dy = dx;
        }
        extendedRate = await client.GetFeatureAsync(0x8061, ct).ConfigureAwait(false) != 0;
        IReadOnlyList<int> wired = [], radio = [], actual = []; int wiredRate = 0, radioRate = 0;
        if (extendedRate)
        {
            wired = DecodeRateMask((await client.FeatureAsync(0x8061, 0, [0], ct).ConfigureAwait(false))[1]);
            radio = DecodeRateMask((await client.FeatureAsync(0x8061, 0, [1], ct).ConfigureAwait(false))[1]);
            actual = DecodeRateMask((await client.FeatureAsync(0x8061, 1, ct: ct).ConfigureAwait(false))[1]);
            wiredRate = RateFromIndex((await client.FeatureAsync(0x8061, 2, [0], ct).ConfigureAwait(false))[0]);
            radioRate = RateFromIndex((await client.FeatureAsync(0x8061, 2, [1], ct).ConfigureAwait(false))[0]);
        }
        else if (await client.GetFeatureAsync(0x8060, ct).ConfigureAwait(false) != 0)
        {
            var mask = (await client.FeatureAsync(0x8060, 0, ct: ct).ConfigureAwait(false))[0];
            actual = Enumerable.Range(1, 8).Where(i => (mask & (1 << (i - 1))) != 0).Select(i => 1000 / i).Distinct().Order().ToArray();
            var interval = (await client.FeatureAsync(0x8060, 1, ct: ct).ConfigureAwait(false))[0];
            if (interval == 0) throw new InvalidDataException("回报间隔不能为零。");
            wired = radio = actual; wiredRate = radioRate = 1000 / interval;
        }
        return State = new(extended, flags, rangeX, rangeY, x, y, dx, dy, lod, wired, radio, actual, wiredRate, radioRate);
    }
    private async Task<IReadOnlyList<int>> ReadExtendedRangeAsync(byte direction, CancellationToken ct)
    {
        var bytes = new List<byte>();
        for (byte page = 0; page < 16; page++)
        {
            var data = await client.FeatureAsync(0x2202, 2, [0, direction, page], ct).ConfigureAwait(false);
            Require(data, 16);
            if (data[1] != direction || data[2] != page) throw new InvalidDataException("DPI 范围回复的页码或坐标轴不匹配。");
            bytes.AddRange(data[3..16]);
            if (Enumerable.Range(0, bytes.Count / 2).Any(i => bytes[i * 2] == 0 && bytes[i * 2 + 1] == 0)) return DecodeRanges(bytes.ToArray());
        }
        throw new InvalidDataException("DPI 范围超过 16 页，未找到结束标记。");
    }
    public static IReadOnlyList<int> DecodeRanges(ReadOnlySpan<byte> bytes)
    {
        var values = new SortedSet<int>(); int previous = 0;
        for (var i = 0; i + 1 < bytes.Length; i += 2)
        {
            var value = Be(bytes, i); if (value == 0) break;
            if (value >= 0xE000)
            {
                var step = value & 0x1FFF;
                if (previous == 0 || step == 0 || i + 3 >= bytes.Length) throw new InvalidDataException("DPI 范围数据无效。");
                var end = Be(bytes, i + 2);
                if (end < previous || end >= 0xE000) throw new InvalidDataException("DPI 范围上限无效。");
                for (var v = previous + step; v <= end; v += step) values.Add(v);
                previous = end; i += 2;
            }
            else { values.Add(value); previous = value; }
        }
        if (values.Count == 0) throw new InvalidDataException("传感器未返回有效 DPI。");
        return values.ToArray();
    }
    public async Task ApplyAsync(int dpiX, int dpiY, int rateHz, CancellationToken ct = default)
    {
        var old = State ?? await ReadAsync(ct).ConfigureAwait(false);
        old.ValidateDpi(dpiX, dpiY);
        if (rateHz != 0 && !old.ActualRates.Contains(rateHz)) throw new ArgumentOutOfRangeException(nameof(rateHz), "当前连接不支持此回报率。");
        try
        {
            await SetDpiAsync((ushort)dpiX, (ushort)dpiY, old, ct).ConfigureAwait(false);
            if (rateHz != 0) await SetRateAsync(rateHz, ct).ConfigureAwait(false);
            var read = await ReadAsync(ct).ConfigureAwait(false);
            if (read.CurrentX != dpiX || old.IndependentAxes && read.CurrentY != dpiY || rateHz != 0 && (wireless ? read.WirelessRate : read.WiredRate) != rateHz)
                throw new IOException("性能设置读回与目标不一致。请退出同时控制鼠标的 G HUB 后重试。");
        }
        catch (Exception error)
        {
            try
            {
                using var recover = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await SetDpiAsync(old.CurrentX, old.CurrentY, old, recover.Token).ConfigureAwait(false);
                var oldRate = wireless ? old.WirelessRate : old.WiredRate;
                if (oldRate != 0) await SetRateAsync(oldRate, recover.Token).ConfigureAwait(false);
            }
            catch (Exception recovery) { throw new AggregateException("性能设置失败，原设置恢复也未完成。", error, recovery); }
            throw;
        }
    }
    public async Task SetDpiAsync(ushort x, ushort y, SensorPerformance state, CancellationToken ct = default)
    {
        state.ValidateDpi(x, y);
        if (state.Extended) await client.FeatureAsync(0x2202, 6, [0, (byte)(x >> 8), (byte)x, state.IndependentAxes ? (byte)(y >> 8) : (byte)0, state.IndependentAxes ? (byte)y : (byte)0, (state.Flags & 2) != 0 ? state.Lod : (byte)0], ct).ConfigureAwait(false);
        else await client.FeatureAsync(0x2201, 3, [0, (byte)(x >> 8), (byte)x], ct).ConfigureAwait(false);
    }
    private async Task SetRateAsync(int hz, CancellationToken ct)
    {
        if (extendedRate) await client.FeatureAsync(0x8061, 3, [(byte)RateIndex(hz)], ct).ConfigureAwait(false);
        else await client.FeatureAsync(0x8060, 2, [(byte)(1000 / hz)], ct).ConfigureAwait(false);
    }
    public static IReadOnlyList<int> DecodeRateMask(byte mask) => Enumerable.Range(0, 7).Where(i => (mask & (1 << i)) != 0).Select(RateFromIndex).ToArray();
    public static int RateFromIndex(int index) => index is >= 0 and <= 6 ? 125 << index : throw new InvalidDataException("未知回报率索引。");
    public static int RateIndex(int hz) => Enumerable.Range(0, 7).FirstOrDefault(i => RateFromIndex(i) == hz, -1) is var index && index >= 0 ? index : throw new ArgumentOutOfRangeException(nameof(hz));
    private static ushort Be(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
    private static void Require(byte[] data, int length) { if (data.Length < length || data[0] != 0) throw new InvalidDataException("传感器回复长度不足或索引不匹配。"); }
}
