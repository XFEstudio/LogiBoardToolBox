using System.Buffers.Binary;

namespace LogiBoardToolBox.Core.HidPlusPlus;

public sealed record HeroCalibrationInfo(byte Width, ushort Length, ushort DpiX, ushort DpiY);
public sealed record AdvancedMouseState(byte SensorFlags, HeroCalibrationInfo? Calibration, byte? ModeStatus,
    bool LightforceSupported, bool SurfaceModeSupported, string CalibrationIssue = "", byte[]? StartupConfiguration = null)
{
    public bool CalibrationSupported => (SensorFlags & 4) != 0 && Calibration is not null;
    public bool Hybrid => (ModeStatus.GetValueOrDefault() & 1) != 0;
    public int SurfaceMode => (ModeStatus.GetValueOrDefault() >> 1) & 3;
    public bool? StartupEnabled => StartupConfiguration is { Length: >= 10 } data ? data[3] switch { 1 => true, 2 => false, _ => null } : null;
}

/// <summary>Device settings use 8090's masked status field; calibration uses 2202, not guessed flash offsets.</summary>
public sealed class AdvancedMouseController(HidPlusPlusClient client, string deviceName)
{
    private readonly SemaphoreSlim writes = new(1, 1);
    public AdvancedMouseState? State { get; private set; }
    public async Task<AdvancedMouseState> ReadAsync(CancellationToken ct = default)
    {
        byte flags = 0; HeroCalibrationInfo? calibration = null; byte? status = null; var issue = "";
        if (await client.GetFeatureAsync(0x2202, ct).ConfigureAwait(false) != 0)
        {
            var caps = await client.FeatureAsync(0x2202, 1, [0], ct).ConfigureAwait(false);
            if (caps.Length < 3 || caps[0] != 0) throw new InvalidDataException("传感器能力回复不匹配。");
            flags = caps[2];
            if ((flags & 4) != 0)
            {
                var onboard = await client.GetFeatureAsync(0x8100, ct).ConfigureAwait(false) != 0
                    && (await client.FeatureAsync(0x8100, 2, ct: ct).ConfigureAwait(false))[0] == 1;
                if (onboard) issue = "板载模式下不能进行 HERO 校准，请先选择软件配置。";
                else
                {
                try
                {
                    var data = await client.FeatureAsync(0x2202, 8, [0], ct).ConfigureAwait(false);
                    if (data.Length < 8 || data[0] != 0) throw new InvalidDataException("HERO 校准参数回复不完整。");
                    calibration = new(data[1], Be(data, 2), Be(data, 4), Be(data, 6));
                }
                catch (HidPlusPlusException error) { issue = "固件声明支持 HERO 校准，但读取校准参数失败：" + error.Message; }
                }
            }
        }
        if (await client.GetFeatureAsync(0x8090, ct).ConfigureAwait(false) != 0)
        {
            var data = await client.FeatureAsync(0x8090, 0, ct: ct).ConfigureAwait(false);
            if (data.Length < 2) throw new InvalidDataException("LIGHTFORCE 状态回复不完整。");
            status = data[1];
        }
        byte[]? startup = null;
        if (await client.GetFeatureAsync(0x8071, ct).ConfigureAwait(false) != 0)
        {
            var rgb = await client.FeatureAsync(0x8071, 0, [255, 255, 0], ct).ConfigureAwait(false);
            if (rgb.Length >= 5 && (rgb[4] & 1) != 0)
            {
                var data = await client.FeatureAsync(0x8071, 3, [0, 0, 1], ct).ConfigureAwait(false);
                if (data.Length >= 10 && data[1] == 0 && data[2] == 1) startup = data[..10];
            }
        }
        var hits = await client.GetFeatureAsync(0x1B0C, ct).ConfigureAwait(false) != 0;
        var supportedModel = deviceName.Contains("G502 X", StringComparison.OrdinalIgnoreCase) || deviceName.Contains("SUPERLIGHT 2", StringComparison.OrdinalIgnoreCase);
        return State = new(flags, calibration, status, status is not null && !hits && supportedModel,
            status is not null && (flags & 2) != 0, issue, startup);
    }
    public async Task SetStartupEffectAsync(bool enabled, CancellationToken ct = default)
    {
        await writes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await ReadAsync(ct).ConfigureAwait(false);
            if (state.StartupConfiguration is not { } data) throw new NotSupportedException("该设备没有提供启动灯效设置。");
            if (state.StartupEnabled == enabled) return;
            var parameters = data.ToArray(); parameters[0] = 1; parameters[3] = (byte)(enabled ? 1 : 2);
            await client.FeatureAsync(0x8071, 3, parameters, ct).ConfigureAwait(false);
            if ((await ReadAsync(ct).ConfigureAwait(false)).StartupEnabled != enabled) throw new IOException("启动灯效设置读回不一致。");
        }
        finally { writes.Release(); }
    }
    public Task SetLightforceAsync(bool hybrid, CancellationToken ct = default) => SetModeAsync(hybrid ? 1 : 0, 1, ct);
    public Task SetSurfaceModeAsync(int mode, CancellationToken ct = default)
    {
        if (mode is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(mode));
        return SetModeAsync(mode << 1, 6, ct);
    }
    private async Task SetModeAsync(int value, byte mask, CancellationToken ct)
    {
        await writes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = State ?? await ReadAsync(ct).ConfigureAwait(false);
            if (mask == 1 ? !state.LightforceSupported : !state.SurfaceModeSupported) throw new NotSupportedException("当前设备不支持此设置。");
            var before = (await client.FeatureAsync(0x8090, 0, ct: ct).ConfigureAwait(false))[1];
            if ((before & mask) == (value & mask)) return;
            var after = (byte)((before & ~mask) | (value & mask));
            await client.FeatureAsync(0x8090, 1, [0, after, 0, mask], ct).ConfigureAwait(false);
            var read = await ReadAsync(ct).ConfigureAwait(false);
            if ((read.ModeStatus.GetValueOrDefault() & mask) != (after & mask)) throw new IOException("设备设置读回不一致，可能有其他软件同时控制鼠标。");
        }
        finally { writes.Release(); }
    }
    public async Task StartCalibrationAsync(byte axis = 0, CancellationToken ct = default, ushort expectedCount = 0)
    {
        var state = State ?? await ReadAsync(ct).ConfigureAwait(false);
        if (!state.CalibrationSupported) throw new NotSupportedException("该固件未提供 HERO 校准能力。");
        if (axis > 1) throw new ArgumentOutOfRangeException(nameof(axis));
        // Software calibration, bounded start/process timeouts. Completion is not inferred from this acknowledgement.
        await client.FeatureAsync(0x2202, 9, [0, axis, (byte)(expectedCount >> 8), (byte)expectedCount, 1, 60, 60, 60], ct).ConfigureAwait(false);
    }
    public async Task SetCalibrationAsync(short correction, byte axis = 0, CancellationToken ct = default)
    {
        if (axis > 1 || correction is < -1023 or > 1023 && correction != short.MinValue) throw new ArgumentOutOfRangeException(nameof(correction));
        if (State?.CalibrationSupported != true) throw new NotSupportedException("该固件未提供 HERO 校准能力。");
        await client.FeatureAsync(0x2202, 10, [0, axis, (byte)(correction >> 8), (byte)correction], ct).ConfigureAwait(false);
    }
    public static short CalculateCorrection(double referenceCounts, double targetCounts, int referenceDpi, int targetCalibrationDpi)
    {
        if (referenceCounts < 100 || targetCounts < 100 || referenceDpi <= 0 || targetCalibrationDpi <= 0) throw new ArgumentException("移动距离不足，不能生成校准。");
        var factor = targetCalibrationDpi * referenceCounts / (referenceDpi * targetCounts);
        var correction = (int)Math.Round((factor - 1) * 1024);
        if (correction is < -1023 or > 1023) throw new InvalidDataException("校准超出传感器范围，请将两只鼠标紧贴并同时移动后重试。");
        return (short)correction;
    }
    private static ushort Be(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset));
}
