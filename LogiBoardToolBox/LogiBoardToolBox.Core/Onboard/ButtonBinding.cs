using System.Buffers.Binary;

namespace LogiBoardToolBox.Core.Onboard;

public static class ButtonBinding
{
    public static byte[] Mouse(int number)
    {
        if (number is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(number));
        var mask = 1 << (number - 1);
        return [0x80, 1, (byte)(mask >> 8), (byte)mask];
    }

    public static byte[] Keyboard(byte modifier, byte usage) => [0x80, 2, modifier, usage];
    public static byte[] Consumer(ushort usage) => [0x80, 3, (byte)(usage >> 8), (byte)usage];
    public static byte[] Special(byte function, byte data = 0) => [0x90, function, 0xFF, data];
    public static byte[] Disabled() => [0x80, 0, 0xFF, 0xFF];
    public static byte[] Macro(ushort sector, ushort address = 0)
    {
        if (sector > 0x0FFF) throw new ArgumentOutOfRangeException(nameof(sector));
        return [(byte)(sector >> 8), (byte)sector, (byte)(address >> 8), (byte)address];
    }

    public static bool IsMacro(ReadOnlySpan<byte> b) => b.Length == 4 && (b[0] >> 4) is 0 or 1;
    public static ushort MacroSector(ReadOnlySpan<byte> b) => (ushort)(((b[0] & 15) << 8) | b[1]);
    public static ushort MacroOffset(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt16BigEndian(b[2..]);
    public static string Describe(byte[] b)
    {
        if (b.SequenceEqual(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF })) return "未分配";
        if (b[0] == 0x20) return "停止所有宏";
        if (IsMacro(b)) return $"板载宏 {MacroSector(b)}:{MacroOffset(b)}";
        if (b[0] == 0x80)
            return b[1] switch
            {
                0 => "禁用",
                1 => ((b[2] << 8) | b[3]) switch { 1 => "左键点击", 2 => "右键点击", 4 => "中键点击", 8 => "后退", 16 => "前进", _ => $"鼠标组合按键 0x{b[2]:X2}{b[3]:X2}" },
                2 => Macros.HidKeys.Describe(b[2], b[3]),
                3 => $"媒体 HID 0x{b[2]:X2}{b[3]:X2}",
                _ => Convert.ToHexString(b)
            };
        if (b[0] == 0x90) return b[1] switch
        {
            1 => "向左滚动", 2 => "向右滚动", 3 => "DPI 增加", 4 => "DPI 减少", 5 => "DPI 循环",
            6 => "默认 DPI", 7 => "DPI Shift", 8 => "下一个配置", 9 => "上一个配置", 10 => "板载配置文件循环",
            11 => "G-Shift", 12 => "电量显示", 13 => $"选择配置 {b[3]}", 16 => "向下滚动", 17 => "向上滚动",
            _ => $"特殊功能 0x{b[1]:X2}"
        };
        return $"原始绑定 {Convert.ToHexString(b)}";
    }
}
