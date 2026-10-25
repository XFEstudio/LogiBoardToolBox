using System.Buffers.Binary;
using LogiBoardToolBox.Core.HidPlusPlus;

namespace LogiBoardToolBox.Core.Lighting;

public sealed record LightingCluster(byte Index, IReadOnlyDictionary<int, byte> Effects);
public sealed record LightsyncCapabilities(IReadOnlyList<LightingCluster> Clusters, IReadOnlyList<byte> LedIds, ushort FeatureId = 0x8071, bool ReadableEffects = false)
{
    public int ZoneCount => LedIds.Count > 0 ? LedIds.Count : Clusters.Count;
}

/// <summary>RGB_EFFECTS + PER_KEY_LIGHTING_V2. Realtime writes always use volatile RAM.</summary>
public sealed class LightsyncDevice(HidPlusPlusClient client, IReadOnlyList<byte>? preferredLedOrder = null)
{
    // GUI points in the G502 X PLUS's light_map.json, mapped to HID++ zone identifiers.
    public static IReadOnlyList<byte> G502XZoneMap { get; } = [3, 7, 2, 4, 8, 6, 5, 1];
    public LightsyncCapabilities? Capabilities { get; private set; }
    private byte[]? originalControl;
    private readonly Dictionary<byte, byte[]> originalEffects = [];
    private IReadOnlyList<byte> ledOrder = [];
    public async Task<LightsyncCapabilities> ReadAsync(CancellationToken ct = default)
    {
        var clusters = new List<LightingCluster>(); var leds = new List<byte>();
        ushort feature = 0x8071;
        if (await client.GetFeatureAsync(feature, ct).ConfigureAwait(false) == 0) feature = 0x8070;
        var readable = false;
        if (await client.GetFeatureAsync(feature, ct).ConfigureAwait(false) != 0)
        {
            var modern = feature == 0x8071;
            var info = await client.FeatureAsync(feature, 0, modern ? [255, 255, 0] : [], ct).ConfigureAwait(false);
            if (info.Length < (modern ? 7 : 5)) throw new InvalidDataException("LIGHTSYNC 灯区信息无效。");
            var count = info[modern ? 2 : 0]; readable = (info[modern ? 6 : 4] & 1) != 0;
            if (count > 16) throw new InvalidDataException("LIGHTSYNC 灯区数量无效。");
            for (byte z = 0; z < count; z++)
            {
                var zone = await client.FeatureAsync(feature, modern ? (byte)0 : (byte)1, modern ? [z, 255, 0] : [z], ct).ConfigureAwait(false);
                var effectCountOffset = modern ? 4 : 3;
                if (zone.Length <= effectCountOffset || zone[0] != z || zone[effectCountOffset] > 64) throw new InvalidDataException("LIGHTSYNC 灯效列表无效。");
                var effects = new Dictionary<int, byte>();
                for (byte e = 0; e < zone[effectCountOffset]; e++)
                {
                    var item = await client.FeatureAsync(feature, modern ? (byte)0 : (byte)2, [z, e, 0], ct).ConfigureAwait(false);
                    if (item.Length < 8 || item[0] != z || item[1] != e) throw new InvalidDataException("LIGHTSYNC 灯效回复不匹配。");
                    effects[BinaryPrimitives.ReadUInt16BigEndian(item.AsSpan(2))] = e;
                }
                clusters.Add(new(z, effects));
            }
        }
        if (feature == 0x8071 && await client.GetFeatureAsync(0x8081, ct).ConfigureAwait(false) != 0)
            for (byte page = 0; page < 3; page++)
            {
                var data = await client.FeatureAsync(0x8081, 0, [0, page], ct).ConfigureAwait(false);
                if (data.Length < 16 || data[0] != 0 || data[1] != page) throw new InvalidDataException("独立 LED 列表回复无效。");
                for (var bit = 0; bit < 112; bit++)
                { var id = page * 112 + bit; if (id is > 0 and < 255 && (data[2 + bit / 8] & (1 << (bit % 8))) != 0) leds.Add((byte)id); }
            }
        ledOrder = preferredLedOrder is { Count: > 0 } && preferredLedOrder.Count == leds.Count && preferredLedOrder.All(leds.Contains)
            ? preferredLedOrder.ToArray() : leds;
        return Capabilities = new(clusters, leds, feature, readable);
    }
    public async Task ClaimAsync(CancellationToken ct = default)
    {
        var caps = Capabilities ?? await ReadAsync(ct).ConfigureAwait(false);
        if (caps.ZoneCount == 0 || caps.Clusters.Any(c => !c.Effects.ContainsKey(1))) throw new NotSupportedException("此鼠标没有可用的可编程 RGB 灯区。");
        originalEffects.Clear();
        if (caps.FeatureId == 0x8070)
        {
            originalControl = await client.FeatureAsync(0x8070, 7, ct: ct).ConfigureAwait(false);
            if (originalControl.Length < 1) throw new IOException("LED 控制状态回复不足。");
            if (caps.ReadableEffects)
                foreach (var c in caps.Clusters)
                {
                    var old = await client.FeatureAsync(0x8070, 14, [c.Index], ct).ConfigureAwait(false);
                    if (old.Length < 12 || old[0] != c.Index) throw new IOException("LED 原始效果回复不匹配。");
                    originalEffects[c.Index] = old[..12];
                }
            else if (originalControl[0] != 0) throw new NotSupportedException("其他软件正在控制此 LED 接口，且固件不能读取原效果。请先释放灯效控制。");
            await client.FeatureAsync(0x8070, 8, [1], ct).ConfigureAwait(false);
        }
        else
        {
            originalControl = await client.FeatureAsync(0x8071, 5, [0], ct).ConfigureAwait(false);
            if (originalControl.Length < 3) throw new IOException("RGB 控制状态回复不足。");
            await client.FeatureAsync(0x8071, 5, [1, 3, 6], ct).ConfigureAwait(false);
        }
        foreach (var cluster in caps.Clusters)
            if (cluster.Effects.TryGetValue(1, out var index))
            {
                var args = new byte[13]; args[0] = cluster.Index; args[1] = index; args[12] = 1;
                if (caps.FeatureId == 0x8070) args[12] = 0; // Legacy persistence flag: RAM only.
                await client.FeatureAsync(caps.FeatureId, caps.FeatureId == 0x8071 ? (byte)1 : (byte)3, args, ct).ConfigureAwait(false);
            }
        await Task.Delay(100, ct).ConfigureAwait(false);
    }
    public async Task SendFrameAsync(IReadOnlyList<Rgb> colors, CancellationToken ct = default)
    {
        var caps = Capabilities ?? throw new InvalidOperationException("请先读取灯区能力。");
        if (originalControl is null) throw new InvalidOperationException("请先取得灯效控制。");
        if (colors.Count != caps.ZoneCount) throw new ArgumentException("颜色数量必须与实际灯区数量一致。");
        if (ledOrder.Count == 0)
        {
            for (var i = 0; i < caps.Clusters.Count; i++)
            {
                var cluster = caps.Clusters[i]; var color = colors[i]; var args = new byte[13];
                args[0] = cluster.Index; args[1] = cluster.Effects[1]; args[2] = color.R; args[3] = color.G; args[4] = color.B;
                args[12] = caps.FeatureId == 0x8071 ? (byte)1 : (byte)0;
                await client.FeatureAsync(caps.FeatureId, caps.FeatureId == 0x8071 ? (byte)1 : (byte)3, args, ct).ConfigureAwait(false);
            }
            return;
        }
        for (var start = 0; start < ledOrder.Count; start += 4)
        {
            var args = new byte[16];
            var count = Math.Min(4, ledOrder.Count - start);
            for (var i = 0; i < count; i++)
            { var c = colors[start + i]; args[i * 4] = ledOrder[start + i]; args[i * 4 + 1] = c.R; args[i * 4 + 2] = c.G; args[i * 4 + 3] = c.B; }
            var reply = await client.FeatureAsync(0x8081, 1, args, ct).ConfigureAwait(false);
            if (reply.Length < 16 || Enumerable.Range(0, count).Any(i => reply[i * 4] != args[i * 4])) throw new IOException("独立灯区写入回复不匹配。");
        }
        await client.FeatureAsync(0x8081, 7, [0, 0, 0, 0, 0], ct).ConfigureAwait(false);
    }
    public async Task PowerAsync(bool on, CancellationToken ct = default)
    {
        if (Capabilities?.FeatureId == 0x8071) await client.FeatureAsync(0x8071, 8, [1, (byte)(on ? 1 : 3), 0], ct).ConfigureAwait(false);
    }
    public async Task ReleaseAsync(CancellationToken ct = default)
    {
        if (originalControl is null) return;
        if (Capabilities?.FeatureId == 0x8070)
        {
            foreach (var old in originalEffects.Values)
            { var args = new byte[13]; old.CopyTo(args, 0); await client.FeatureAsync(0x8070, 3, args, ct).ConfigureAwait(false); }
            await client.FeatureAsync(0x8070, 8, [originalControl[0]], ct).ConfigureAwait(false);
        }
        else
        {
            await PowerAsync(true, ct).ConfigureAwait(false);
            await client.FeatureAsync(0x8071, 5, [1, originalControl[1], originalControl[2]], ct).ConfigureAwait(false);
        }
        originalControl = null;
    }
}
