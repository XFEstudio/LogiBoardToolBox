using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogiBoardToolBox.Core.Devices;

namespace LogiBoardToolBox.Core.Macros;

public sealed record GHubMacroBinding(string SlotId, int ButtonIndex, bool GShift, string CardId,
    string Name, string Script, MacroRepeat Repeat, string HoldScript, string ReleaseScript,
    string Color, IReadOnlyList<int> RecordedDelays, CompiledMacro Compiled);

public sealed record GHubMacroImport(string ProfileId, string ProfileName, string DeviceKey,
    IReadOnlyList<GHubMacroBinding> Bindings)
{
    /// <summary>Imports assigned sequence macros only. Other profile fields remain on the mouse.</summary>
    public static GHubMacroImport Read(string json, string deviceKey = "g502x-plus")
    {
        if (json.Length > 8 * 1024 * 1024) throw new InvalidDataException("G HUB 配置文件过大。");
        var model = MouseCatalog.BySlotPrefix(deviceKey) ?? throw new NotSupportedException("此型号尚未核对 G HUB 按键槽位，不能直接导入。");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var last = root.GetProperty("profiles/auto_switching/s_last_used_profile").GetString();
        var candidates = root.GetProperty("profiles").GetProperty("profiles").EnumerateArray()
            .Where(p => Text(p, "id") == last || Text(p, "applicationId") == last && Flag(p, "activeForApplication")).ToArray();
        if (candidates.Length != 1) throw new InvalidDataException("无法唯一确定 G HUB 最后使用的配置。");
        var profile = candidates[0];
        var cards = root.GetProperty("cards").GetProperty("cards").EnumerateArray().ToDictionary(c => Text(c, "id"));
        var bindings = new List<GHubMacroBinding>();
        var seen = new HashSet<string>();
        foreach (var assignment in profile.GetProperty("assignments").EnumerateArray())
        {
            var slot = Text(assignment, "slotId");
            if (!slot.StartsWith(deviceKey + "_", StringComparison.Ordinal)) continue;
            if (!seen.Add(slot)) throw new InvalidDataException($"G HUB 按键 {slot} 有重复分配。");
            var match = Regex.Match(slot, "^" + Regex.Escape(deviceKey) + @"_(g[1-9][0-9]?|scroll[12])_m1(_shifted)?$");
            if (!match.Success) continue; // Sensitivity and lighting cards are outside this import.
            var cardId = Text(assignment, "cardId");
            if (!cards.TryGetValue(cardId, out var card))
            {
                if (!cardId.StartsWith("0f82f693-5b78-4cf5-867e-", StringComparison.Ordinal))
                    throw new InvalidDataException($"按键 {slot} 引用的 G HUB 卡片不存在。");
                continue; // Built-in mouse functions already present on the device are preserved.
            }
            if (Text(card, "attribute") != "MACRO_PLAYBACK") continue;
            if (!model.TryGetButtonIndex(match.Groups[1].Value, out var index))
                throw new NotSupportedException($"{model.DisplayName} 的宏槽位 {slot} 尚未核对，停止导入以免绑定到其他实体键。");
            var name = Text(card, "name");
            var macro = card.GetProperty("macro");
            if (Text(macro, "type") != "SEQUENCE") throw new NotSupportedException($"宏“{name}”不是可导入的录制序列。");
            var sequence = macro.GetProperty("sequence");
            var flags = new[] { Flag(sequence, "useSimpleActions"), Flag(sequence, "useRepeatActions"), Flag(sequence, "useToggleActions") };
            if (flags.Count(f => f) > 1) throw new InvalidDataException($"宏“{name}”有冲突的重复模式。");
            var repeat = flags[0] ? MacroRepeat.Once : flags[1] ? MacroRepeat.WhileHeld : flags[2] ? MacroRepeat.Toggle : MacroRepeat.Sequence;
            var delays = new List<int>();
            var press = ReadPhase(sequence, repeat switch { MacroRepeat.Once => "simpleSequence", MacroRepeat.WhileHeld => "heldSequence", MacroRepeat.Toggle => "toggleSequence", _ => "pressSequence" }, delays);
            var hold = repeat == MacroRepeat.Sequence ? ReadPhase(sequence, "heldSequence", delays) : "";
            var release = repeat == MacroRepeat.Sequence ? ReadPhase(sequence, "releaseSequence", delays) : "";
            if (repeat != MacroRepeat.Sequence && (HasSteps(sequence, "pressSequence") || HasSteps(sequence, "releaseSequence")))
                throw new NotSupportedException($"宏“{name}”包含额外的按下/松开片段，不能丢弃片段后导入。");
            // G HUB serializes actual delay components. defaultDelay is editor metadata;
            // adding a uniform delay would change recorded down/up and inter-click timings.
            var compiled = MacroCompiler.CompileProgram(press, repeat, hold, release).ToOnboard();
            var color = macro.TryGetProperty("color", out var c) ? Text(c, "hex") : "#00A5DB";
            bindings.Add(new(slot, index, match.Groups[2].Success, cardId, name, press, repeat, hold, release,
                color, delays.ToArray(), compiled));
        }
        if (bindings.Count == 0) throw new InvalidDataException("当前配置没有为此鼠标分配录制宏。");
        return new(Text(profile, "id"), Text(profile, "name"), deviceKey, bindings);
    }

    private static bool HasSteps(JsonElement sequence, string key) => sequence.TryGetProperty(key, out var phase)
        && phase.TryGetProperty("components", out var components) && components.GetArrayLength() != 0;

    private static string ReadPhase(JsonElement sequence, string key, List<int> delays)
    {
        if (!sequence.TryGetProperty(key, out var phase) || !phase.TryGetProperty("components", out var components)) return "";
        var lines = new List<string>();
        foreach (var component in components.EnumerateArray())
        {
            var fields = component.EnumerateObject().ToArray();
            if (fields.Length != 1) throw new NotSupportedException("宏步骤必须包含一种明确的操作。");
            var field = fields[0];
            switch (field.Name)
            {
                case "delay":
                    var duration = field.Value.TryGetProperty("durationMs", out var ms) ? ms.GetInt32() : 0;
                    if (duration < 0) throw new InvalidDataException("录制延迟不能为负数。");
                    delays.Add(duration);
                    for (var remaining = duration; remaining > 0;)
                    {
                        var part = Math.Min(remaining, ushort.MaxValue); lines.Add($"delay {part}"); remaining -= part;
                        if (lines.Count > 10000) throw new InvalidDataException("宏指令数超过限制。");
                    }
                    break;
                case "keyboard":
                    var usage = Usage(field.Value, 255);
                    lines.Add($"key 0x{usage:X2} {(Flag(field.Value, "isDown") ? "down" : "up")}");
                    break;
                case "mouse":
                    if (!field.Value.TryGetProperty("button", out var button)) throw new NotSupportedException("此鼠标操作尚不能保持原样导入。");
                    lines.Add($"mouse {Usage(button, 16)} {(Flag(button, "isDown") ? "down" : "up")}");
                    break;
                default: throw new NotSupportedException($"宏步骤 {field.Name} 尚不能保持原样导入。");
            }
            if (lines.Count > 10000) throw new InvalidDataException("宏指令数超过限制。");
        }
        return string.Join('\n', lines);
    }

    private static int Usage(JsonElement element, int maximum)
    {
        var property = element.GetProperty("hidUsage");
        var value = property.ValueKind == JsonValueKind.String ? int.Parse(property.GetString()!, CultureInfo.InvariantCulture) : property.GetInt32();
        if (value < 1 || value > maximum) throw new NotSupportedException($"不支持的 HID usage {value}。");
        return value;
    }
    private static bool Flag(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    private static string Text(JsonElement element, string key) => element.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
}
