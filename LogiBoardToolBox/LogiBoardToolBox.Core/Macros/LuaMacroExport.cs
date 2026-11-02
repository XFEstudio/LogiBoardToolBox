using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LogiBoardToolBox.Core.Macros;

/// <summary>Exports standalone G HUB Lua. Canonical metadata is accepted only when the entire generated source still matches.</summary>
public static class LuaMacroExport
{
    private const string Header = "-- LogiBoardToolBox Lua Export v1\n";
    private const string MetadataPrefix = "-- LBX-META:";
    private const string ChecksumPrefix = "-- LBX-SHA256:";
    private sealed record Envelope(int Version, string Name, int Trigger, MacroRepeat Repeat, byte[][] Press, byte[][] Hold, byte[][] Release);
    public static string Export(string name, MacroProgram program, int triggerButton = 1)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (triggerButton is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(triggerButton));
        if (!Enum.IsDefined(program.Repeat)) throw new ArgumentOutOfRangeException(nameof(program));
        if (name is null || name.Length > 256) throw new ArgumentException("宏名称不能超过 256 字符。", nameof(name));
        if (program.InstructionCount is < 1 or > 10000) throw new FormatException("待导出的宏指令数应为 1–10,000。");
        var envelope = new Envelope(1, name, triggerButton, program.Repeat, Copy(program.Press), Copy(program.Hold), Copy(program.Release));
        var json = JsonSerializer.SerializeToUtf8Bytes(envelope);
        var source = new StringBuilder(Header)
            .Append("-- ").AppendLine(name.Replace('\r', ' ').Replace('\n', ' '))
            .AppendLine("-- Standalone G HUB Lua; buttons: 1 left, 2 middle, 3 right.")
            .AppendLine("-- Repeats use 1ms physical-state polling; pulses shorter than the polling interval cannot be observed.")
            .AppendLine("-- The metadata preserves the source macro only while ALL generated code remains unedited.")
            .AppendLine("EnablePrimaryMouseButtonEvents(true)")
            .AppendLine($"local __lb_trigger = {triggerButton}")
            .AppendLine($"local __lb_mode = {(int)program.Repeat}")
            .AppendLine("local __lb_keys, __lb_mouse = {}, {}")
            .AppendLine("local __lb_active, __lb_last, __lb_released, __lb_cancel = false, true, false, false")
            .AppendLine("local __lb_ignore, __lb_phase = 0, 'press'")
            .AppendLine("local function __lb_poll()")
            .AppendLine("  local down = IsMouseButtonPressed(__lb_trigger)")
            .AppendLine("  if __lb_last and not down then __lb_released = true end")
            .AppendLine("  if not __lb_last and down then")
            .AppendLine("    __lb_ignore = __lb_ignore + 1")
            .AppendLine("    if __lb_mode == 2 then __lb_cancel = true end")
            .AppendLine("  end")
            .AppendLine("  __lb_last = down")
            .AppendLine("  if __lb_mode == 2 then return not __lb_cancel end")
            .AppendLine("  if __lb_mode == 1 or __lb_phase == 'hold' then return not __lb_released end")
            .AppendLine("  return true")
            .AppendLine("end")
            .AppendLine("local function __lb_sleep(ms)")
            .AppendLine("  local finish = GetRunningTime() + ms")
            .AppendLine("  repeat")
            .AppendLine("    if not __lb_poll() then return false end")
            .AppendLine("    local remaining = finish - GetRunningTime()")
            .AppendLine("    if remaining <= 0 then return true end")
            .AppendLine("    Sleep(math.min(1, remaining))")
            .AppendLine("  until false")
            .AppendLine("end")
            .AppendLine("local function __lb_key(code, down)")
            .AppendLine("  if down and not __lb_keys[code] then PressKey(code) __lb_keys[code] = true")
            .AppendLine("  elseif not down and __lb_keys[code] then ReleaseKey(code) __lb_keys[code] = nil end")
            .AppendLine("end")
            .AppendLine("local function __lb_button(code, down)")
            .AppendLine("  if down and not __lb_mouse[code] then PressMouseButton(code) __lb_mouse[code] = true")
            .AppendLine("  elseif not down and __lb_mouse[code] then ReleaseMouseButton(code) __lb_mouse[code] = nil end")
            .AppendLine("end")
            .AppendLine("local function __lb_copy(t) local copy={} for k,v in pairs(t) do copy[k]=v end return copy end")
            .AppendLine("local function __lb_restore(keys, buttons)")
            .AppendLine("  for k in pairs(__lb_keys) do if not keys[k] then __lb_key(k,false) end end")
            .AppendLine("  for k in pairs(__lb_mouse) do if not buttons[k] then __lb_button(k,false) end end")
            .AppendLine("  for k in pairs(keys) do __lb_key(k,true) end")
            .AppendLine("  for k in pairs(buttons) do __lb_button(k,true) end")
            .AppendLine("end");
        EmitPhase(source, "press", program.Press);
        EmitPhase(source, "hold", program.Hold);
        EmitPhase(source, "release", program.Release);
        source.AppendLine("function OnEvent(event, arg)")
            .AppendLine("  if event == 'PROFILE_DEACTIVATED' then __lb_cancel=true __lb_restore({}, {}) return end")
            .AppendLine("  if event ~= 'MOUSE_BUTTON_PRESSED' or arg ~= __lb_trigger then return end")
            .AppendLine("  if __lb_ignore > 0 then __lb_ignore=__lb_ignore-1 return end")
            .AppendLine("  if __lb_active then return end")
            .AppendLine("  __lb_active=true __lb_last=true __lb_released=false __lb_cancel=false __lb_phase='press'")
            .AppendLine("  local ok = __lb_press()")
            .AppendLine("  if __lb_mode == 3 and ok then")
            .AppendLine("    local baselineKeys, baselineButtons = __lb_copy(__lb_keys), __lb_copy(__lb_mouse)")
            .AppendLine("    __lb_phase='hold'")
            .AppendLine("    while __lb_poll() do")
            .AppendLine("      if not __lb_hold() or not __lb_sleep(1) then break end")
            .AppendLine("    end")
            .AppendLine("    __lb_restore(baselineKeys, baselineButtons)")
            .AppendLine("    __lb_phase='release'")
            .AppendLine("    __lb_release()")
            .AppendLine("  elseif (__lb_mode == 1 or __lb_mode == 2) and ok then")
            .AppendLine("    while __lb_sleep(1) do if not __lb_press() then break end end")
            .AppendLine("  end")
            .AppendLine("  __lb_restore({}, {})")
            .AppendLine("  __lb_active=false")
            .AppendLine("end")
            .Append(MetadataPrefix).AppendLine(System.Convert.ToBase64String(json))
            .Append(ChecksumPrefix).AppendLine(System.Convert.ToHexString(SHA256.HashData(json)).ToLowerInvariant());
        // Canonical generation is independent of the host newline convention.
        var result = source.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        if (result.Length > LuaMacroRuntime.MaximumSourceLength) throw new NotSupportedException("导出源码超过 4 MiB 字符，请拆分为较短的宏；未生成无法重新导入的脚本。");
        return result;
    }
    private static byte[][] Copy(IReadOnlyList<byte[]> instructions) => instructions.Select(i => i.ToArray()).ToArray();
    private static void EmitPhase(StringBuilder source, string phase, IReadOnlyList<byte[]> instructions)
    {
        source.Append("local function __lb_").Append(phase).AppendLine("()");
        foreach (var bytes in instructions)
        {
            if (bytes.Length == 0 || bytes.Length != MacroCompiler.InstructionSize(bytes[0])) throw new FormatException("宏指令长度无效，不能导出。");
            source.AppendLine("  if not __lb_poll() then return false end");
            void Key(int usage, bool down)
            {
                int scan;
                try { scan = LuaKeyboardCodes.FromHidUsage((byte)usage); }
                catch (FormatException error) { throw new NotSupportedException($"HID 按键 0x{usage:X2} 尚无已核对的 G HUB Lua 扫描码，未导出。", error); }
                source.AppendLine($"  __lb_key({scan}, {(down ? "true" : "false")})");
            }
            switch (bytes[0])
            {
                case 1:
                    source.AppendLine("  while not __lb_released do if not __lb_sleep(1) then return false end end"); break;
                case 0x40:
                    source.AppendLine($"  if not __lb_sleep({BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(1))}) then return false end"); break;
                case 0x43 or 0x44:
                    var down = bytes[0] == 0x43;
                    if (down) for (var bit = 0; bit < 8; bit++) if ((bytes[1] & (1 << bit)) != 0) Key(224 + bit, true);
                    if (bytes[2] != 0) Key(bytes[2], down);
                    if (!down) for (var bit = 7; bit >= 0; bit--) if ((bytes[1] & (1 << bit)) != 0) Key(224 + bit, false);
                    break;
                case 0x41 or 0x42:
                    var mask = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(1));
                    if ((mask & ~31) != 0) throw new NotSupportedException("G HUB Lua 导出仅支持鼠标按钮 1–5，未忽略其他按键。");
                    for (var button = 1; button <= 5; button++) if ((mask & (1 << (button - 1))) != 0) source.AppendLine($"  __lb_button({LuaKeyNames.MouseButton(button)}, {(bytes[0] == 0x41 ? "true" : "false")})");
                    break;
                case 0x20: source.AppendLine($"  MoveMouseWheel({unchecked((sbyte)bytes[1])})"); break;
                case 0x61: source.AppendLine($"  MoveMouseRelative({BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(1))}, {BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(3))})"); break;
                default: throw new NotSupportedException($"指令 0x{bytes[0]:X2}（媒体、横向滚动或存储跳转等）没有已验证的 G HUB Lua 等价接口，未导出。");
            }
        }
        source.AppendLine("  return true").AppendLine("end");
    }

    internal static bool TryRestore(string source, int trigger, MacroRepeat requested, out LuaOnboardConversion conversion)
    {
        conversion = null!;
        var normalized = source.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith(Header, StringComparison.Ordinal)) return false;
        var lines = normalized.Split('\n');
        if (lines.Length < 4 || !lines[^3].StartsWith(MetadataPrefix, StringComparison.Ordinal) || !lines[^2].StartsWith(ChecksumPrefix, StringComparison.Ordinal)) return false;
        Envelope? envelope;
        try
        {
            var json = System.Convert.FromBase64String(lines[^3][MetadataPrefix.Length..]);
            if (json.Length > 262144 || !string.Equals(System.Convert.ToHexString(SHA256.HashData(json)), lines[^2][ChecksumPrefix.Length..], StringComparison.OrdinalIgnoreCase)) return false;
            envelope = JsonSerializer.Deserialize<Envelope>(json);
            if (envelope is null || envelope.Version != 1 || envelope.Press is null || envelope.Hold is null || envelope.Release is null || envelope.Trigger != trigger) return false;
        }
        catch (Exception error) when (error is FormatException or JsonException) { return false; }
        var program = new MacroProgram(envelope.Repeat, envelope.Press, envelope.Hold, envelope.Release);
        string canonical;
        try { canonical = Export(envelope.Name, program, trigger); }
        catch (Exception error) when (error is ArgumentException or FormatException or NotSupportedException or InvalidDataException or NullReferenceException) { return false; }
        if (!string.Equals(normalized, canonical, StringComparison.Ordinal)) return false;
        if (requested != MacroRepeat.Once && requested != program.Repeat) throw new NotSupportedException("此导出 Lua 已明确编码播放方式，不能用其他播放方式覆盖脚本行为。");
        conversion = new LuaOnboardConversion(Script(program.Press), program.Repeat, trigger, "完整规范导出源码与元数据逐字一致，已恢复原宏；编辑过的 Lua 不会套用旧元数据。")
        { HoldScript = Script(program.Hold), ReleaseScript = Script(program.Release), CanonicalProgram = program };
        _ = MacroCompiler.CompileProgram(conversion.Script, conversion.Repeat, conversion.HoldScript, conversion.ReleaseScript);
        return true;
    }
    private static string Script(IReadOnlyList<byte[]> instructions)
    {
        var lines = new List<string>();
        foreach (var bytes in instructions)
            switch (bytes[0])
            {
                case 1: lines.Add("wait-release"); break;
                case 0x40: lines.Add("delay " + BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(1))); break;
                case 0x43 or 0x44:
                    var usage = bytes[2];
                    if (usage is >= 224 and <= 231 && (bytes[1] & (1 << (usage - 224))) != 0) usage = 0;
                    lines.Add($"key {HidKeys.Describe(bytes[1], usage)} {(bytes[0] == 0x43 ? "down" : "up")}"); break;
                case 0x41 or 0x42:
                    var mask = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(1));
                    for (var i = 0; i < 16; i++) if ((mask & (1 << i)) != 0) lines.Add($"mouse {i + 1} {(bytes[0] == 0x41 ? "down" : "up")}"); break;
                case 0x20: lines.Add("wheel " + unchecked((sbyte)bytes[1])); break;
                case 0x61: lines.Add($"move {BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(1))} {BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(3))}"); break;
                default: throw new NotSupportedException("规范导出包含不支持的指令。");
            }
        return string.Join('\n', lines);
    }
}
