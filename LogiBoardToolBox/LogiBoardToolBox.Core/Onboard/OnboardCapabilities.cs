using System.Buffers.Binary;

namespace LogiBoardToolBox.Core.Onboard;

public sealed record OnboardCapabilities(byte MemoryModel, byte ProfileFormat, byte MacroFormat,
    byte ProfileCount, byte FactoryProfileCount, byte ButtonCount, byte SectorCount,
    ushort SectorSize, byte MechanicalLayout, byte VariousInfo)
{
    public bool HasGShift => (MechanicalLayout & 3) == 2;
    public bool HasDpiShift => ((MechanicalLayout >> 2) & 3) == 2;
    // Format 10 is observed on this G502 X PLUS, not yet covered by upstream codecs.
    // Only the matching geometry and the unchanged button/name regions are enabled,
    // behind an explicit experimental switch in the write planner.
    public bool IsExperimental => ProfileFormat == 10;
    public bool CanEditPerformance => CanEditProfiles;
    public bool CanEditLighting => CanEditProfiles && ProfileFormat >= 2;
    public int BindingRecordCount => IsExperimental ? 13 : 16;
    public int AssignableButtonCount => IsExperimental ? Math.Min(BindingRecordCount, ButtonCount + 2) : ButtonCount;
    public bool HasRecognizedLayout => ProfileFormat is >= 1 and <= 5 ||
        ProfileFormat == 10 && ProfileCount == 5 && ButtonCount == 11 && SectorCount == 16 && SectorSize == 255 && MechanicalLayout == 0x0A;
    public bool CanEditProfiles => MemoryModel == 1 && HasRecognizedLayout &&
        SectorSize is >= 254 and <= 4096 && ButtonCount is > 0 and <= 16 &&
        ProfileCount is > 0 and <= 16 && SectorCount > ProfileCount && (ProfileCount + 1) * 4 + 2 <= SectorSize;
    public bool CanWriteMacros => CanEditProfiles && MacroFormat == 1 && SectorCount > ProfileCount + 1;

    public static OnboardCapabilities Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 11) throw new IOException("板载能力回复长度不足。");
        return new(bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5], bytes[6],
            BinaryPrimitives.ReadUInt16BigEndian(bytes[7..]), bytes[9], bytes[10]);
    }

    public string Summary => $"格式 {ProfileFormat} · {ProfileCount} 个配置 · {ButtonCount} 个按键 · {SectorCount} × {SectorSize} 字节 · 宏格式 {MacroFormat}";
}
