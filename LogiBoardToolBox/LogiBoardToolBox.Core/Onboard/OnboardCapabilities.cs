using System.Buffers.Binary;

namespace LogiBoardToolBox.Core.Onboard;

public sealed record OnboardCapabilities(byte MemoryModel, byte ProfileFormat, byte MacroFormat,
    byte ProfileCount, byte FactoryProfileCount, byte ButtonCount, byte SectorCount,
    ushort SectorSize, byte MechanicalLayout, byte VariousInfo)
{
    // LBT binding-action 3, revision 2, is declared by our hash-bound MCU patch.
    // It is never inferred from the model name, opcode 03, or a UI selection.
    public byte NativeToggleVersion { get; private init; }
    public byte NativeLuaVersion { get; private init; }
    private bool HasResearchGeometry => MemoryModel == 1 && ProfileFormat == 2 &&
        MacroFormat == 1 && ProfileCount == 5 && FactoryProfileCount == 5 && ButtonCount == 11 &&
        SectorCount == 16 && SectorSize == 256 && MechanicalLayout == 0x0A && VariousInfo == 1;
    public bool SupportsNativeToggle => (NativeToggleVersion == 2 || NativeLuaVersion == 1) && HasResearchGeometry;
    public bool SupportsNativeLua => NativeLuaVersion == 1 && HasResearchGeometry;
    public bool HasSameMacroExecution(OnboardCapabilities other) => SupportsNativeToggle == other.SupportsNativeToggle && SupportsNativeLua == other.SupportsNativeLua;
    public bool HasGShift => (MechanicalLayout & 3) == 2;
    public bool HasDpiShift => ((MechanicalLayout >> 2) & 3) == 2;
    // Format 10 is observed on this G502 X PLUS, not yet covered by upstream codecs.
    // Only the matching geometry and the unchanged button/name regions are enabled,
    // behind an explicit experimental switch in the write planner.
    public bool IsExperimental => ProfileFormat == 10;
    public bool UsesAdvancedProfile => ProfileFormat == 7;
    public bool HasIndependentAxes => UsesAdvancedProfile || IsExperimental;
    public bool HasSeparateRates => HasIndependentAxes;
    public bool CanEditPerformance => CanEditProfiles;
    // Format 7 active RGB records require the device's 0x8071 cluster and effect descriptors.
    public bool CanEditLighting => CanEditProfiles && ProfileFormat >= 2 && !UsesAdvancedProfile;
    public int BindingRecordCount => IsExperimental ? 13 : UsesAdvancedProfile ? 12 : 16;
    public int AssignableButtonCount => IsExperimental ? Math.Min(BindingRecordCount, ButtonCount + 2) : ButtonCount;
    public bool HasRecognizedLayout => ProfileFormat is >= 1 and <= 5 ||
        ProfileFormat == 7 && SectorSize == 255 && ProfileCount is > 0 and <= 5 && FactoryProfileCount <= ProfileCount &&
        ButtonCount is > 0 and <= 12 && SectorCount > ProfileCount + 1 && (MechanicalLayout & 3) is 0 or 2 ||
        ProfileFormat == 10 && ProfileCount == 5 && ButtonCount == 11 && SectorCount == 16 && SectorSize == 255 && MechanicalLayout == 0x0A;
    public bool CanEditProfiles => MemoryModel == 1 && HasRecognizedLayout &&
        SectorSize is >= 254 and <= 4096 && ButtonCount is > 0 and <= 16 &&
        ProfileCount is > 0 and <= 16 && SectorCount > ProfileCount && (ProfileCount + 1) * 4 + 2 <= SectorSize;
    public bool CanWriteMacros => CanEditProfiles && MacroFormat == 1 && SectorCount > ProfileCount + 1;
    // Some G304/G305 firmware reports six sectors but rejects addresses 3–5.
    // The read results must also confirm the boundary before treating it as smaller.
    internal bool HasG305StorageGeometry => MemoryModel == 1 && ProfileFormat == 3 && MacroFormat == 1 &&
        ProfileCount == 1 && FactoryProfileCount == 1 && ButtonCount == 6 && SectorCount == 6 &&
        SectorSize == 255 && MechanicalLayout == 0x0A && VariousInfo == 2;
    // Captured HERO (256), G402 (1024), G502 LIGHTSPEED and G102 LIGHTSYNC (255):
    // profile/directory pages have CRCs; macro pages are raw format-1 streams
    // with full-address continuation records. Match the reported geometry,
    // never infer this from a bad CRC or erase/reseal a captured macro page.
    public bool UsesLegacyMacroPages => MemoryModel == 1 && MacroFormat == 1 && MechanicalLayout == 0x0A &&
        (ProfileFormat == 2 && SectorSize == 256 && ProfileCount == 5 && FactoryProfileCount == 5 && ButtonCount == 11 && SectorCount == 16 ||
         ProfileFormat == 3 && SectorSize == 255 && ProfileCount == 5 && FactoryProfileCount == 1 && ButtonCount == 11 && SectorCount == 16 && VariousInfo == 4 ||
         ProfileFormat == 4 && SectorSize == 255 && ProfileCount == 1 && FactoryProfileCount == 1 && ButtonCount == 6 && SectorCount == 16 && VariousInfo == 1 ||
         ProfileFormat == 1 && SectorSize == 1024 && ProfileCount == 1 && FactoryProfileCount == 1 && ButtonCount == 8 && SectorCount == 3);
    public bool IsRawMacroSector(int sector) => UsesLegacyMacroPages && sector > ProfileCount && sector < SectorCount;
    public int MacroPageCapacity => SectorSize - (UsesLegacyMacroPages ? 0 : 2);
    public int MacroJumpSize => UsesLegacyMacroPages ? 5 : 3;
    public bool IsValidSectorWrite(int sector, ReadOnlySpan<byte> data, bool allowErased = false) => data.Length == SectorSize &&
        (IsRawMacroSector(sector) || SectorChecksum.IsValid(data) || allowErased && sector > ProfileCount && sector < SectorCount && SectorChecksum.IsErased(data));

    public static OnboardCapabilities Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 11) throw new IOException("板载能力回复长度不足。");
        return new(bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5], bytes[6],
            BinaryPrimitives.ReadUInt16BigEndian(bytes[7..]), bytes[9], bytes[10])
        {
            NativeToggleVersion = bytes.Length >= 16 && bytes.Slice(11, 4).SequenceEqual(new byte[] { 0x4C, 0x42, 0x54, 3 })
                ? bytes[15] : (byte)0,
            NativeLuaVersion = bytes.Length >= 16 && bytes.Slice(11, 4).SequenceEqual(new byte[] { 0x4C, 0x42, 0x54, 4 })
                ? bytes[15] : (byte)0
        };
    }

    public string Summary => $"格式 {ProfileFormat} · {ProfileCount} 个配置 · {ButtonCount} 个按键 · {SectorCount} × {SectorSize} 字节 · 宏格式 {MacroFormat}";
}
