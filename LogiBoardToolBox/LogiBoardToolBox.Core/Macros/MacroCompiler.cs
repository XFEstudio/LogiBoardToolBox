using System.Buffers.Binary;
using System.Globalization;
using LogiBoardToolBox.Core.Onboard;

namespace LogiBoardToolBox.Core.Macros;

public enum MacroRepeat { Once, WhileHeld, Toggle, Sequence }
public sealed record CompiledMacro(IReadOnlyList<byte[]> Instructions, MacroRepeat Repeat)
{
    public byte[] Bytes => Instructions.SelectMany(x => x).ToArray();
    public int Length => Instructions.Sum(x => x.Length);
}

/// <summary>Compiles into macro format 1 bytecode executed by the mouse firmware.</summary>
public static class MacroCompiler
{
    public const string OnboardToggleUnavailable = "触发宏需要再次按同一按键停止；当前板载协议尚未实现这一取消机制。不能把持续循环指令当成触发宏写入，也不会改成按住循环。请在软件配置中使用触发宏。";

    public static CompiledMacro Compile(string script, MacroRepeat repeat = MacroRepeat.Once, OnboardCapabilities? capabilities = null)
    {
        if (!Enum.IsDefined(repeat)) throw new ArgumentOutOfRangeException(nameof(repeat));
        if (repeat == MacroRepeat.Sequence) throw new NotSupportedException("请使用 CompileProgram 提供序列的点击、按住和松开片段。");
        if (repeat == MacroRepeat.Toggle && capabilities?.SupportsNativeToggle != true) throw new NotSupportedException(OnboardToggleUnavailable);
        var output = Parse(script, new HashSet<string>());
        if (output.Count == 0) throw new FormatException("请先添加宏步骤。");
        if (repeat == MacroRepeat.WhileHeld) output.Add([2]);
        if (repeat == MacroRepeat.Toggle) output.Add([3]);
        output.Add([0xFF]);
        return new(output, repeat);
    }

    private static List<byte[]> Parse(string script, HashSet<string> held, bool requireReleased = true)
    {
        var output = new List<byte[]>();
        var lineNumber = 0;
        foreach (var original in script.Replace("\r", "").Split('\n'))
        {
            lineNumber++;
            var line = original.Split('#', 2)[0].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var command = parts[0].ToLowerInvariant();
            try
            {
                switch (command)
                {
                    case "tap" when parts.Length == 2:
                        var tap = HidKeys.ParseChord(parts[1]);
                        if (tap.Usage != 0 && held.Contains($"hid:{tap.Usage}")) throw new FormatException("不能击键一个尚未松开的按键。");
                        // A tap inside a sequence must not release modifiers held by its press phase.
                        var transientModifiers = tap.Modifier;
                        for (var bit = 0; bit < 8; bit++) if (held.Contains($"hid:{224 + bit}")) transientModifiers &= (byte)~(1 << bit);
                        AddKeyboard(output, true, transientModifiers, tap.Usage);
                        AddKeyboard(output, false, transientModifiers, tap.Usage);
                        break;
                    case "key" when parts.Length == 3:
                        var key = HidKeys.ParseChord(parts[1]);
                        var down = Direction(parts[2]);
                        for (var bit = 0; bit < 8; bit++) if ((key.Modifier & (1 << bit)) != 0) Track(held, $"hid:{224 + bit}", down);
                        if (key.Usage != 0) Track(held, $"hid:{key.Usage}", down);
                        AddKeyboard(output, down, key.Modifier, key.Usage);
                        break;
                    case "delay" when parts.Length == 2:
                        var delay = Number(parts[1], 1, 65535);
                        output.Add([0x40, (byte)(delay >> 8), (byte)delay]);
                        break;
                    case "mouse" when parts.Length == 3:
                        var button = Number(parts[1], 1, 16);
                        var mouseDown = Direction(parts[2]);
                        Track(held, $"mouse:{button}", mouseDown);
                        var mask = 1 << (button - 1);
                        output.Add([(byte)(mouseDown ? 0x41 : 0x42), (byte)(mask >> 8), (byte)mask]);
                        break;
                    case "consumer" when parts.Length == 3:
                        var consumer = Number(parts[1], 1, 65535);
                        var consumerDown = Direction(parts[2]);
                        Track(held, $"consumer:{consumer}", consumerDown);
                        output.Add([(byte)(consumerDown ? 0x45 : 0x46), (byte)(consumer >> 8), (byte)consumer]);
                        break;
                    case "wheel" or "pan" when parts.Length == 2:
                        output.Add([(byte)(command == "wheel" ? 0x20 : 0x21), unchecked((byte)(sbyte)Number(parts[1], -127, 127))]);
                        break;
                    case "move" when parts.Length == 3:
                        var xy = new byte[5];
                        xy[0] = 0x61;
                        BinaryPrimitives.WriteInt16BigEndian(xy.AsSpan(1), (short)Number(parts[1], short.MinValue, short.MaxValue));
                        BinaryPrimitives.WriteInt16BigEndian(xy.AsSpan(3), (short)Number(parts[2], short.MinValue, short.MaxValue));
                        output.Add(xy);
                        break;
                    case "wait-release" when parts.Length == 1:
                        output.Add([1]);
                        break;
                    default:
                        throw new FormatException("支持 tap、key、delay、mouse、consumer、wheel、pan、move 和 wait-release。请检查参数个数。");
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                throw new FormatException($"宏第 {lineNumber} 行：{ex.Message}", ex);
            }
            if (output.Count > 10000) throw new FormatException("宏指令数超过限制。");
        }
        if (requireReleased && held.Count != 0) throw new FormatException("宏结束前必须释放所有按键：" + string.Join(", ", held));
        return output;
    }

    public static MacroProgram CompileProgram(string press, MacroRepeat repeat, string hold = "", string release = "", ushort standardDelay = 0)
    {
        if (!Enum.IsDefined(repeat)) throw new ArgumentOutOfRangeException(nameof(repeat));
        var held = new HashSet<string>();
        var first = Parse(press, held, repeat != MacroRepeat.Sequence);
        var initialHeld = held.ToHashSet();
        var middle = repeat == MacroRepeat.Sequence ? Parse(hold, held, false) : [];
        if (!initialHeld.SetEquals(held)) throw new FormatException("按住片段每轮结束时需恢复到点击片段结束时的按键状态。");
        var last = repeat == MacroRepeat.Sequence ? Parse(release, held) : [];
        if (first.Count + middle.Count + last.Count == 0) throw new FormatException("请先添加宏步骤。");
        List<byte[]> Delay(List<byte[]> list) => standardDelay == 0 ? list : WithStandardDelay(new(list, repeat), standardDelay).Instructions.ToList();
        return new(repeat, Delay(first), Delay(middle), Delay(last));
    }

    private static void AddKeyboard(List<byte[]> output, bool down, byte modifier, byte usage)
    {
        var opcode = (byte)(down ? 0x43 : 0x44);
        if (usage != 0) { output.Add([opcode, modifier, usage]); return; }
        // G HUB's native format-1 compiler retains the HID usage for a modifier
        // event (for example Shift is 43 02 E1), rather than emitting usage 00.
        for (var bit = 0; bit < 8; bit++)
            if ((modifier & (1 << bit)) != 0) output.Add([opcode, (byte)(1 << bit), (byte)(0xE0 + bit)]);
    }

    private static bool Direction(string text) => text.ToLowerInvariant() switch
    { "down" => true, "up" => false, _ => throw new FormatException("按键方向必须是 down 或 up。") };
    private static void Track(HashSet<string> held, string key, bool down)
    {
        if (down ? !held.Add(key) : !held.Remove(key)) throw new FormatException($"{key} 重复按下或未按下就释放。");
    }
    public static int Number(string text, int min, int max)
    {
        var number = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : int.Parse(text, CultureInfo.InvariantCulture);
        if (number < min || number > max) throw new FormatException($"数值必须在 {min} 到 {max} 之间。");
        return number;
    }

    public static int InstructionSize(byte opcode) => opcode switch
    {
        0 or 1 or 2 or 3 or 0xFF => 1,
        0x20 or 0x21 => 2,
        >= 0x40 and <= 0x46 or 0x60 => 3,
        0x61 => 5,
        _ => throw new InvalidDataException($"未知板载宏指令 0x{opcode:X2}；无法安全分析其存储占用。")
    };

    public static CompiledMacro WithStandardDelay(CompiledMacro macro, ushort milliseconds)
    {
        if (milliseconds == 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        var output = new List<byte[]>();
        for (var i = 0; i < macro.Instructions.Count; i++)
        {
            var current = macro.Instructions[i]; output.Add(current);
            if (current[0] is >= 0x41 and <= 0x46 && i + 1 < macro.Instructions.Count && macro.Instructions[i + 1][0] != 0x40)
                output.Add([0x40, (byte)(milliseconds >> 8), (byte)milliseconds]);
        }
        return new(output, macro.Repeat);
    }
}
