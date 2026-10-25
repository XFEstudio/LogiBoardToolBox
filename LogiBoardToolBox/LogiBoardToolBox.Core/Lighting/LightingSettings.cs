namespace LogiBoardToolBox.Core.Lighting;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Parse(string color)
    {
        var value = color.Trim().TrimStart('#');
        if (value.Length != 6) throw new FormatException("颜色需要六位 RGB，例如 #00B8D9。");
        var bytes = Convert.FromHexString(value); return new(bytes[0], bytes[1], bytes[2]);
    }
    public Rgb Scale(double value) => new((byte)Math.Clamp(Math.Round(R * value), 0, 255), (byte)Math.Clamp(Math.Round(G * value), 0, 255), (byte)Math.Clamp(Math.Round(B * value), 0, 255));
    public static Rgb Lerp(Rgb a, Rgb b, double t) => new((byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
    public static Rgb Hue(double hue)
    {
        var h = (hue - Math.Floor(hue)) * 6; var c = (int)h; var f = h - c;
        var up = (byte)Math.Round(255 * f); var down = (byte)(255 - up);
        return c switch { 0 => new(255, up, 0), 1 => new(down, 255, 0), 2 => new(0, 255, up), 3 => new(0, down, 255), 4 => new(up, 0, 255), _ => new(255, 0, down) };
    }
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

public enum LightsyncMode { Preset, Freestyle, Animation }
public enum LightingEffect { Off, Fixed, Cycle, Breathing, Wave, Starlight, Ripple, ScreenSampler, AudioVisualizer, Signature }
public sealed record LightingFrame(int Milliseconds, Rgb[] Colors, bool Smooth = true, TransitionCurve Curve = TransitionCurve.Linear);
public sealed record SampleRegion(double X, double Y, double Width, double Height)
{
    public void Validate()
    {
        if (!double.IsFinite(X + Y + Width + Height) || X < 0 || Y < 0 || Width <= 0 || Height <= 0 || X + Width > 1.00001 || Y + Height > 1.00001)
            throw new FormatException("屏幕采样区域使用 0–100% 坐标，且必须位于所选屏幕内。");
    }
}
public sealed record LightingSettings
{
    public string Name { get; init; } = "自定义动画";
    public AnimationDirection Direction { get; init; }
    public int DefaultFrameSpeed { get; init; } = 100;
    public LightsyncMode Mode { get; init; }
    public LightingEffect Effect { get; init; } = LightingEffect.Cycle;
    public Rgb Color { get; init; } = new(0, 184, 217);
    public Rgb Background { get; init; } = new(10, 0, 30);
    public int Brightness { get; init; } = 100;
    public int Period { get; init; } = 4000;
    public bool Reverse { get; init; }
    public bool Loop { get; init; } = true;
    public double AudioGain { get; init; } = 3;
    public int MonitorIndex { get; init; }
    public Rgb[] ZoneColors { get; init; } = Enumerable.Range(0, 8).Select(i => Rgb.Hue(i / 8.0)).ToArray();
    public LightingFrame[] Frames { get; init; } = [];
    public SampleRegion[] Regions { get; init; } = Enumerable.Range(0, 8).Select(i => new SampleRegion(i / 8.0, 0, 1 / 8.0, 1)).ToArray();
    public bool RequiresScreen => Mode == LightsyncMode.Preset && Effect == LightingEffect.ScreenSampler;
    public bool RequiresAudio => Mode == LightsyncMode.Preset && Effect == LightingEffect.AudioVisualizer;
    public bool CanStoreProfile => Mode == LightsyncMode.Preset && Effect is LightingEffect.Off or LightingEffect.Fixed or LightingEffect.Cycle or LightingEffect.Breathing or LightingEffect.Signature;
    public LightingSettings ForZoneCount(int count)
    {
        if (count is < 1 or > 32 || ZoneColors.Length == 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == ZoneColors.Length && Regions.Length == count && Frames.All(f => f.Colors.Length == count)) return this;
        Rgb[] Resize(Rgb[] colors) => Enumerable.Range(0, count).Select(i => colors[count == 1 ? 0 : (int)Math.Round(i * (colors.Length - 1.0) / (count - 1))]).ToArray();
        return this with
        {
            ZoneColors = Resize(ZoneColors), Frames = Frames.Select(f => f with { Colors = Resize(f.Colors) }).ToArray(),
            Regions = Enumerable.Range(0, count).Select(i => new SampleRegion(i / (double)count, 0, 1.0 / count, 1)).ToArray()
        };
    }
    public void Validate()
    {
        if (!Enum.IsDefined(Mode) || !Enum.IsDefined(Effect) || !Enum.IsDefined(Direction) || DefaultFrameSpeed is < 50 or > 1000 || Brightness is < 0 or > 100 || Period is < 100 or > 65535 || !double.IsFinite(AudioGain) || AudioGain is < 0.1 or > 20 || MonitorIndex < 0)
            throw new FormatException("灯效亮度、速度或音频增益无效。");
        if (ZoneColors.Length is < 1 or > 32 || Regions.Length != ZoneColors.Length) throw new FormatException("颜色与采样区域必须匹配实际灯区数量（1–32）。");
        foreach (var region in Regions) region.Validate();
        if (Mode == LightsyncMode.Animation)
        {
            if (Frames.Length is < 1 or > 256 || Frames.Any(f => f.Colors.Length != ZoneColors.Length || !Enum.IsDefined(f.Curve) || f.Milliseconds < 0 || f.Milliseconds >= Period) || Frames.Select(f => f.Milliseconds).Distinct().Count() != Frames.Length || Frames.Min(f => f.Milliseconds) != 0)
                throw new FormatException("动画需包含 0 毫秒首帧，各帧时间不能重复，且必须小于周期；每帧颜色数量必须与灯区一致。");
        }
    }
}

public static class LightingRenderer
{
    public static Rgb[] Render(LightingSettings settings, double elapsedMs, IReadOnlyList<Rgb>? screen = null, double audio = 0, double lastPressMs = double.NegativeInfinity)
    {
        var s = settings; var count = s.ZoneColors.Length; var t = Math.Max(0, elapsedMs) / s.Period; if (s.Reverse) t = -t;
        Rgb[] colors;
        if (s.Mode == LightsyncMode.Freestyle) colors = s.ZoneColors.ToArray();
        else if (s.Mode == LightsyncMode.Animation)
        {
            var frames = s.Frames.OrderBy(f => f.Milliseconds).ToArray();
            if (frames.Length == 0) return new Rgb[count];
            var position = s.Loop ? (elapsedMs % s.Period + s.Period) % s.Period : Math.Clamp(elapsedMs, 0, s.Period);
            if (s.Direction == AnimationDirection.Bounce) { var phase = Math.Max(0, elapsedMs) % (2 * s.Period); position = phase <= s.Period ? phase : 2 * s.Period - phase; }
            if (s.Reverse ^ s.Direction == AnimationDirection.ReverseCycle) position = s.Period - position;
            var before = frames.LastOrDefault(f => f.Milliseconds <= position) ?? frames[0];
            var after = frames.FirstOrDefault(f => f.Milliseconds > position);
            var end = after?.Milliseconds ?? s.Period;
            after ??= s.Loop ? frames[0] : before;
            var fraction = before.Smooth && end > before.Milliseconds ? LightingTransition.Evaluate(before.Curve, (position - before.Milliseconds) / (end - before.Milliseconds)) : 0;
            if (s.Direction == AnimationDirection.Random)
            {
                // Random order must still honor each selected frame's edited duration.
                // A long deterministic shuffle makes independent previews and device frames agree.
                var order = new int[Math.Max(16, frames.Length * 4)]; ulong seed = 1442695040888963407;
                for (var n = 1; n < order.Length; n++)
                {
                    seed = unchecked(seed * 6364136223846793005UL + 1442695040888963407UL);
                    order[n] = (int)((seed >> 32) % (uint)frames.Length);
                    if (frames.Length > 1 && order[n] == order[n - 1]) order[n] = (order[n] + 1) % frames.Length;
                }
                var durations = frames.Select((frame, i) => (i + 1 < frames.Length ? frames[i + 1].Milliseconds : s.Period) - frame.Milliseconds).ToArray();
                var cycle = order.Sum(i => durations[i]); var remaining = Math.Max(0, elapsedMs) % cycle; var slot = 0;
                while (slot + 1 < order.Length && remaining >= durations[order[slot]]) remaining -= durations[order[slot++]];
                before = frames[order[slot]]; after = frames[order[(slot + 1) % order.Length]];
                fraction = before.Smooth ? LightingTransition.Evaluate(before.Curve, remaining / durations[order[slot]]) : 0;
            }
            colors = Enumerable.Range(0, count).Select(i => Rgb.Lerp(before.Colors[i], after.Colors[i], fraction)).ToArray();
        }
        else colors = Enumerable.Range(0, count).Select(i => s.Effect switch
        {
            LightingEffect.Off => default,
            LightingEffect.Fixed => s.Color,
            LightingEffect.Cycle => Rgb.Hue(t),
            LightingEffect.Breathing => s.Color.Scale((1 - Math.Cos(t * Math.Tau)) / 2),
            LightingEffect.Wave => Rgb.Hue(t + i / (double)count),
            LightingEffect.Signature => Rgb.Hue(t + i / (2.0 * count)).Scale(0.4 + 0.6 * Math.Pow(Math.Sin((t + i / (double)count) * Math.PI), 2)),
            LightingEffect.Starlight => Rgb.Lerp(s.Background, s.Color, Math.Pow(Math.Max(0, Math.Sin(t * Math.Tau * (1 + i * .13) + i * 13.7)), 12)),
            LightingEffect.Ripple => Rgb.Lerp(s.Background, s.Color, Math.Clamp(1 - Math.Abs(Math.Abs(i - (count - 1) / 2.0) - (elapsedMs - lastPressMs) * count / s.Period), 0, 1)),
            LightingEffect.ScreenSampler => screen?.Count == count ? screen[i] : default,
            LightingEffect.AudioVisualizer => Rgb.Lerp(s.Background, s.Color, Math.Clamp(audio * s.AudioGain * count - i, 0, 1)),
            _ => default
        }).ToArray();
        return colors.Select(c => c.Scale(s.Brightness / 100.0)).ToArray();
    }
}
