using System.Buffers.Binary;

namespace LogiBoardToolBox.Core.Onboard;

public static class OnboardLighting
{
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
