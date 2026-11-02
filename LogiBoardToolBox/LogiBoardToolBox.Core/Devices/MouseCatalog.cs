using System.Text.Json;
using System.Text.RegularExpressions;

namespace LogiBoardToolBox.Core.Devices;

public sealed record MouseModel(string Id, string GHubModelId, string SlotPrefix, string DisplayName,
    string[] Aliases, ushort[] ProductIds, string ImageAsset, double ImageAspect, string Layout, string[] ButtonNames, int LightingZoneCount)
{
    public bool IsKnown => Id != "Unknown";
    public bool HasLighting => LightingZoneCount > 0;
    public bool HasSideView => Layout == "G502X";
    public string ButtonName(int index) => index < ButtonNames.Length ? ButtonNames[index] : $"按键 {index + 1}";
    public bool TryGetButtonIndex(string slot, out int index)
    {
        index = -1;
        if (Layout == "G502X" && slot is "scroll1" or "scroll2") { index = slot == "scroll1" ? 12 : 11; return true; }
        if (slot.Length > 1 && slot[0] == 'g' && int.TryParse(slot.AsSpan(1), out var number) && number >= 1 && number <= (Layout == "G502X" ? 11 : ButtonNames.Length))
        { index = number - 1; return true; }
        return false;
    }
}

/// <summary>Identity, labels and G HUB namespaces only. Flash geometry always comes from 0x8100.</summary>
public static class MouseCatalog
{
    public static IReadOnlyList<MouseModel> Models { get; } = Load();
    public static MouseModel Unknown { get; } = new("Unknown", "", "", "Logitech Mouse", [], [], "", .53, "Generic", [], 0);
    private static IReadOnlyList<MouseModel> Load()
    {
        using var stream = typeof(MouseCatalog).Assembly.GetManifestResourceStream("LogiBoardToolBox.Core.Devices.mice.json")
            ?? throw new InvalidOperationException("鼠标型号目录缺失。");
        return JsonSerializer.Deserialize<MouseModel[]>(stream) ?? throw new InvalidDataException("鼠标型号目录无效。");
    }
    public static MouseModel? BySlotPrefix(string prefix) => string.IsNullOrWhiteSpace(prefix) ? null : Models.SingleOrDefault(m => m.SlotPrefix == prefix);
    public static MouseModel? ById(string id) => Models.SingleOrDefault(m => m.Id == id);
    public static MouseModel Resolve(IEnumerable<ushort> transportProductIds, string name)
    {
        var ids = transportProductIds.ToHashSet();
        var identified = Models.Where(m => m.ProductIds.Any(ids.Contains)).ToArray();
        if (identified.Length == 1) return identified[0];
        var normalized = Normalize(name);
        // Exact aliases prevent SUPERLIGHT 2 or G502 X from inheriting an older model's layout.
        var named = Models.Where(m => m.Aliases.Any(a => Normalize(a) == normalized)).ToArray();
        return named.Length == 1 ? named[0] : Unknown;
    }
    private static string Normalize(string text)
    {
        var value = Regex.Replace(text.ToUpperInvariant(), @"\b(LOGITECH|GAMING|MOUSE)\b|罗技", " ");
        return Regex.Replace(value, @"\s+", " ").Trim();
    }
    public static IReadOnlyList<ushort> ReadTransportProductIds(ReadOnlySpan<byte> firmwareInfo)
    {
        if (firmwareInfo.Length < 13) return [];
        var ids = new List<ushort>(); var offset = 7;
        foreach (var mask in new[] { 1, 2, 4, 8 })
        {
            if ((firmwareInfo[6] & mask) == 0) continue;
            if (offset + 2 > firmwareInfo.Length) return [];
            ids.Add((ushort)((firmwareInfo[offset] << 8) | firmwareInfo[offset + 1])); offset += 2;
        }
        return ids;
    }
}
