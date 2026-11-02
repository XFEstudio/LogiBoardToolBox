namespace LogiBoardToolBox.Core.Macros;

/// <summary>G HUB Lua Appendix A scan codes mapped to USB keyboard usages; independent of the OS layout.</summary>
public static class LuaKeyboardCodes
{
    // Source: installed G_series_Lua_API.pdf, Appendix A pp.43-45,
    // SHA256 0e9f5532708c8e6d811eededa2dbba458d9f891bf3b5f503346dd93079052f52.
    // Numeric PressKey parameters use this scan-code namespace, not USB HID values.
    private static readonly IReadOnlyDictionary<int, byte> ScanCodes = Create();
    private static readonly IReadOnlyDictionary<byte, int> HidUsages = ScanCodes.GroupBy(pair => pair.Value)
        .ToDictionary(group => group.Key, group => group.First().Key);

    public static bool TryToHidUsage(int scanCode, out byte usage) => ScanCodes.TryGetValue(scanCode, out usage);
    public static bool TryFromHidUsage(byte usage, out int scanCode) => HidUsages.TryGetValue(usage, out scanCode);

    public static int FromHidUsage(byte usage) => TryFromHidUsage(usage, out var scanCode) ? scanCode
        : throw new FormatException($"HID 按键 0x{usage:X2} 没有已核对的 G HUB Lua 扫描码，无法等价导出。");

    public static byte ToHidUsage(int scanCode) => TryToHidUsage(scanCode, out var usage) ? usage
        : throw new FormatException($"未知的 G HUB Lua 键盘扫描码 0x{scanCode:X}；数字参数是扫描码，不能直接当作 HID 键码。");

    public static byte ToHidUsage(double scanCode)
    {
        if (!double.IsFinite(scanCode) || scanCode != Math.Truncate(scanCode) || scanCode < 0 || scanCode > int.MaxValue)
            throw new FormatException("G HUB Lua 键盘扫描码必须是已核对的非负整数。");
        return ToHidUsage((int)scanCode);
    }

    private static IReadOnlyDictionary<int, byte> Create()
    {
        var map = new Dictionary<int, byte>
        {
            [0x01] = 0x29, // Escape
            [0x0B] = 0x27, [0x0C] = 0x2D, [0x0D] = 0x2E, [0x0E] = 0x2A, [0x0F] = 0x2B,
            [0x1A] = 0x2F, [0x1B] = 0x30, [0x1C] = 0x28, [0x1D] = 0xE0,
            [0x27] = 0x33, [0x28] = 0x34, [0x29] = 0x35, [0x2A] = 0xE1, [0x2B] = 0x31,
            [0x33] = 0x36, [0x34] = 0x37, [0x35] = 0x38, [0x36] = 0xE5,
            [0x38] = 0xE2, [0x39] = 0x2C, [0x3A] = 0x39,
            [0x45] = 0x53, [0x46] = 0x47,
            [0x47] = 0x5F, [0x48] = 0x60, [0x49] = 0x61, [0x4A] = 0x56,
            [0x4B] = 0x5C, [0x4C] = 0x5D, [0x4D] = 0x5E, [0x4E] = 0x57,
            [0x4F] = 0x59, [0x50] = 0x5A, [0x51] = 0x5B, [0x52] = 0x62, [0x53] = 0x63,
            [0x56] = 0x64, // Non-US backslash / ISO key
            [0x57] = 0x44, [0x58] = 0x45, [0x76] = 0x73, // F11, F12, F24
            // Only the extended values explicitly listed in Appendix A are accepted.
            [0x11C] = 0x58, [0x11D] = 0xE4, [0x135] = 0x54, [0x137] = 0x46, [0x138] = 0xE6,
            [0x146] = 0x48, [0x147] = 0x4A, [0x148] = 0x52, [0x149] = 0x4B,
            [0x14B] = 0x50, [0x14D] = 0x4F, [0x14F] = 0x4D, [0x150] = 0x51,
            [0x151] = 0x4E, [0x152] = 0x49, [0x153] = 0x4C,
            [0x15B] = 0xE3, [0x15C] = 0xE7, [0x15D] = 0x65,
        };
        for (var digit = 1; digit <= 9; digit++) map[1 + digit] = (byte)(0x1D + digit);
        for (var function = 1; function <= 10; function++) map[0x3A + function] = (byte)(0x39 + function);
        for (var function = 13; function <= 23; function++) map[0x64 + function - 13] = (byte)(0x68 + function - 13);
        AddRow(0x10, "QWERTYUIOP"); AddRow(0x1E, "ASDFGHJKL"); AddRow(0x2C, "ZXCVBNM");
        return map;

        void AddRow(int first, string keys)
        { for (var index = 0; index < keys.Length; index++) map[first + index] = (byte)(4 + keys[index] - 'A'); }
    }
}
