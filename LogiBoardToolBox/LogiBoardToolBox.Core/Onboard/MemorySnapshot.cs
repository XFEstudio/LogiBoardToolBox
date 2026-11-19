namespace LogiBoardToolBox.Core.Onboard;

public sealed class MemorySnapshot
{
    public const string CurrentFormat = "LogiBoardToolBox.Onboard.v1";
    public const string LegacyFormat = "LogiBordToolBox.Onboard.v1";
    public static bool IsKnownFormat(string? format) => format is CurrentFormat or LegacyFormat;
    public string Format { get; set; } = CurrentFormat;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string DeviceIdentity { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public byte[] Description { get; set; } = [];
    public byte Mode { get; set; }
    public ushort ActiveSector { get; set; }
    public Dictionary<int, byte[]> Sectors { get; set; } = [];
    public Dictionary<int, string> ReadErrors { get; set; } = [];
    public HashSet<int> RejectedSectorAddresses { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public OnboardCapabilities Capabilities => OnboardCapabilities.Parse(Description);

    [System.Text.Json.Serialization.JsonIgnore]
    public int WritableSectorCount
    {
        get
        {
            var caps = Capabilities;
            return caps.HasG305StorageGeometry && RejectedSectorAddresses.SetEquals([3, 4, 5]) &&
                Enumerable.Range(0, 3).All(s => Sectors.TryGetValue(s, out var data) && data.Length == caps.SectorSize) &&
                Enumerable.Range(3, 3).All(s => !Sectors.ContainsKey(s) && ReadErrors.ContainsKey(s))
                ? 3 : caps.SectorCount;
        }
    }

    internal bool HasWritableReadErrors => ReadErrors.Keys.Any(s => s < WritableSectorCount);

    public static IReadOnlyList<(ushort Sector, bool Enabled)> ParseDirectory(byte[] bytes, int maxProfiles)
    {
        if (!SectorChecksum.IsValid(bytes)) return [];
        var result = new List<(ushort, bool)>();
        for (var i = 0; i <= maxProfiles && i * 4 + 3 < bytes.Length - 2; i++)
        {
            var sector = (ushort)((bytes[i * 4] << 8) | bytes[i * 4 + 1]);
            if (sector == 0xFFFF) return result;
            if (i == maxProfiles || sector == 0 || bytes[i * 4 + 2] > 1 || result.Any(x => x.Item1 == sector)) return [];
            result.Add((sector, bytes[i * 4 + 2] != 0));
        }
        return [];
    }

    public IReadOnlyList<MouseProfile> GetProfiles()
    {
        var caps = Capabilities;
        if (!caps.CanEditProfiles) return [];
        var directory = Sectors.TryGetValue(0, out var bytes) ? ParseDirectory(bytes, caps.ProfileCount) : [];
        var profiles = new List<MouseProfile>();
        for (ushort slot = 1; slot <= caps.ProfileCount; slot++)
        {
            var entry = directory.FirstOrDefault(e => e.Sector == slot);
            if (entry.Sector != 0 && Sectors.TryGetValue(slot, out var raw) && SectorChecksum.IsValid(raw))
                profiles.Add(new(slot, entry.Enabled, raw, profileFormat: caps.ProfileFormat));
            else
            {
                var factorySector = 0x100 + Math.Min(slot, caps.FactoryProfileCount);
                if (caps.FactoryProfileCount == 0 || !Sectors.TryGetValue(factorySector, out var factory) || !SectorChecksum.IsValid(factory))
                    throw new InvalidDataException($"配置 {slot} 和对应出厂配置均无法读取；请先用 OMM 初始化板载配置。");
                profiles.Add(new(slot, directory.Count == 0 ? slot == 1 : entry.Enabled, factory, fromFactory: true, profileFormat: caps.ProfileFormat));
            }
        }
        return profiles;
    }
}
