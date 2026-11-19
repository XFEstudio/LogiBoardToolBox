using System.Buffers.Binary;
using LogiBoardToolBox.Core.Lighting;

namespace LogiBoardToolBox.Core.Onboard;

public static class OnboardLighting
{
    public static byte? EffectId(LightingEffect effect) => effect switch
    {
        LightingEffect.Off => 0,
        LightingEffect.Fixed => 1,
        LightingEffect.Cycle => 3,
        LightingEffect.Breathing => 10,
        LightingEffect.Signature => 15,
        _ => null
    };

    public static bool HasActiveRgbRecords(OnboardCapabilities profile, LightsyncCapabilities? device) =>
        profile.UsesAdvancedProfile && profile.CanEditProfiles &&
        device is { FeatureId: 0x8071, Clusters.Count: > 0 and <= 2 } &&
        device.Clusters.All(cluster => cluster.Index < 2) &&
        device.Clusters.Select(cluster => cluster.Index).Distinct().Count() == device.Clusters.Count;

    public static bool CanStoreActiveRgbPreset(OnboardCapabilities profile, LightsyncCapabilities? device,
        LightingEffect effect, int brightness)
    {
        var mode = EffectId(effect);
        return HasActiveRgbRecords(profile, device) && mode is 0 or 1 or 3 or 10 &&
            device!.Clusters.All(cluster => cluster.Effects.ContainsKey(mode.Value) &&
                (brightness > 0 || cluster.Effects.ContainsKey(0)));
    }

    internal static void ApplyActiveRgbPreset(MouseProfile profile, OnboardCapabilities caps,
        LightsyncCapabilities device, LightingSettings preset)
    {
        preset.Validate();
        if (preset.Mode != LightsyncMode.Preset || !CanStoreActiveRgbPreset(caps, device, preset.Effect, preset.Brightness))
            throw new NotSupportedException("当前设备的板载灯区不支持此预设。");
        // A profile record stores the effect ID, while live 0x8071 commands use the cluster's effect index.
        var effect = Create(EffectId(preset.Effect)!.Value, preset.Color.R, preset.Color.G, preset.Color.B,
            (ushort)preset.Period, (byte)preset.Brightness);
        if (effect[0] == 1) effect[4] = 2; // Fixed color with no ramp, as stored by PRO 2 LIGHTSPEED.
        foreach (var cluster in device.Clusters)
            profile.SetActiveRgbLighting(cluster.Index, effect);
    }

    public static byte[] Create(byte mode, byte red, byte green, byte blue, ushort period, byte brightness)
    {
        if (mode is not (0 or 1 or 3 or 10 or 15 or 16) || brightness > 100) throw new ArgumentOutOfRangeException(nameof(mode));
        var bytes = new byte[11];
        if (brightness == 0) return bytes;
        bytes[0] = mode;
        if (mode is 1 or 10) { bytes[1] = red; bytes[2] = green; bytes[3] = blue; }
        if (mode == 1) { bytes[1] = (byte)(red * brightness / 100); bytes[2] = (byte)(green * brightness / 100); bytes[3] = (byte)(blue * brightness / 100); }
        if (mode == 10) { BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), period); bytes[6] = 1; bytes[7] = brightness; }
        if (mode is 3 or 15 or 16) { BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), period); bytes[8] = brightness; }
        return bytes;
    }
}
