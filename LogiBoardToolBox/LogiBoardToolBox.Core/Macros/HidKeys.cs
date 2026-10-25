using System.Globalization;

namespace LogiBoardToolBox.Core.Macros;

public static class HidKeys
{
    private static readonly Dictionary<string, byte> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = 0x28, ["Escape"] = 0x29, ["Esc"] = 0x29, ["Backspace"] = 0x2A,
        ["Tab"] = 0x2B, ["Space"] = 0x2C, ["Minus"] = 0x2D, ["Equal"] = 0x2E,
        ["LeftBracket"] = 0x2F, ["RightBracket"] = 0x30, ["Backslash"] = 0x31,
        ["Semicolon"] = 0x33, ["Quote"] = 0x34, ["Grave"] = 0x35,
        ["Comma"] = 0x36, ["Period"] = 0x37, ["Slash"] = 0x38, ["CapsLock"] = 0x39,
        ["PrintScreen"] = 0x46, ["ScrollLock"] = 0x47, ["Pause"] = 0x48,
        ["Insert"] = 0x49, ["Home"] = 0x4A, ["PageUp"] = 0x4B, ["Delete"] = 0x4C,
        ["End"] = 0x4D, ["PageDown"] = 0x4E, ["Right"] = 0x4F, ["Left"] = 0x50,
        ["Down"] = 0x51, ["Up"] = 0x52, ["NumLock"] = 0x53,
        ["NumpadDivide"] = 0x54, ["NumpadMultiply"] = 0x55, ["NumpadSubtract"] = 0x56,
        ["NumpadAdd"] = 0x57, ["NumpadEnter"] = 0x58, ["Numpad0"] = 0x62,
        ["NumpadDecimal"] = 0x63, ["Menu"] = 0x65
    };
    private static readonly Dictionary<string, byte> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = 1, ["Control"] = 1, ["LeftCtrl"] = 1, ["Shift"] = 2, ["LeftShift"] = 2,
        ["Alt"] = 4, ["LeftAlt"] = 4, ["Win"] = 8, ["Meta"] = 8, ["LeftWin"] = 8,
        ["RightCtrl"] = 16, ["RightShift"] = 32, ["RightAlt"] = 64, ["RightWin"] = 128
    };
    static HidKeys()
    {
        for (var i = 0; i < 26; i++) Keys[((char)('A' + i)).ToString()] = (byte)(4 + i);
        for (var i = 1; i <= 9; i++) { Keys[i.ToString()] = (byte)(0x1D + i); Keys[$"Numpad{i}"] = (byte)(0x58 + i); }
        Keys["0"] = 0x27;
        for (var i = 1; i <= 12; i++) Keys[$"F{i}"] = (byte)(0x39 + i);
        for (var i = 13; i <= 24; i++) Keys[$"F{i}"] = (byte)(0x68 + i - 13);
    }

    public static (byte Modifier, byte Usage) ParseChord(string text)
    {
        byte modifier = 0, usage = 0;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(string.IsNullOrWhiteSpace)) throw new FormatException("快捷键不能为空，例如 Ctrl+Shift+A。");
        foreach (var part in parts)
        {
            if (Modifiers.TryGetValue(part, out var mask)) modifier |= mask;
            else
            {
                if (usage != 0) throw new FormatException("一个快捷键只能包含一个普通键；多个键请使用宏。");
                if (!Keys.TryGetValue(part, out usage))
                {
                    if (!part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || !byte.TryParse(part.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out usage) || usage == 0)
                        throw new FormatException($"未知键名 {part}。也可使用 HID 十六进制键码，例如 0x04。");
                }
                if (usage is >= 0xE0 and <= 0xE7) { modifier |= (byte)(1 << (usage - 0xE0)); usage = 0; }
            }
        }
        if (modifier == 0 && usage == 0) throw new FormatException("快捷键不能为空。");
        return (modifier, usage);
    }

    public static string Describe(byte modifier, byte usage)
    {
        string[] names = ["Ctrl", "Shift", "Alt", "Win", "RightCtrl", "RightShift", "RightAlt", "RightWin"];
        var parts = Enumerable.Range(0, 8).Where(i => (modifier & (1 << i)) != 0).Select(i => names[i]).ToList();
        if (usage != 0) parts.Add(Keys.FirstOrDefault(k => k.Value == usage).Key ?? $"0x{usage:X2}");
        return string.Join('+', parts);
    }
}
