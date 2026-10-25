using System.Buffers.Binary;
using System.Text;

namespace LogiBoardToolBox.Core.Onboard;

/// <summary>Edits an existing sector in place so unknown fields, animations and padding survive.</summary>
public sealed class MouseProfile
{
    private readonly byte[] raw;
    public ushort Sector { get; }
    public bool Enabled { get; set; }
    public bool FromFactory { get; }
    public string DisplayName => $"配置 {Sector} · {(string.IsNullOrWhiteSpace(Name) ? "未命名" : Name)}{(Enabled ? "" : "（未启用）")}";
    public string SlotLabel => $"储存位 {Sector}";
    public string GHubDisplayName => !string.IsNullOrWhiteSpace(Name) ? Name : Sector == 1 ? "桌面: 默认" : $"板载配置 {Sector}";

    public MouseProfile(ushort sector, bool enabled, byte[] data, bool fromFactory = false)
    {
        if (data.Length < 254 || !SectorChecksum.IsValid(data)) throw new InvalidDataException($"配置扇区 {sector} 的 CRC 无效。");
        Sector = sector;
        Enabled = enabled;
        FromFactory = fromFactory;
        raw = (byte[])data.Clone();
    }

    public byte ReportInterval { get => raw[0]; set => raw[0] = value; }
    public byte DefaultDpiIndex { get => raw[1]; set => raw[1] = value; }
    public byte ShiftDpiIndex { get => raw[2]; set => raw[2] = value; }
    public ushort[] Dpis
    {
        get => Enumerable.Range(0, 5).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(3 + 2 * i))).ToArray();
        set
        {
            if (value.Length != 5) throw new ArgumentException("必须提供五个 DPI 槽；0 表示未使用。");
            for (var i = 0; i < 5; i++) BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(3 + i * 2), value[i]);
        }
    }
    public string Name
    {
        get => Encoding.Unicode.GetString(raw, 160, 48).TrimEnd('\0', '\uFFFF');
        set
        {
            if (value.Length > 24) throw new ArgumentException("配置名称最多 24 个 UTF-16 字符。");
            if (Name == value) return;
            Array.Clear(raw, 160, 48);
            Encoding.Unicode.GetBytes(value).CopyTo(raw, 160);
        }
    }

    // Observed format-10 extension: Y table after the 13 normal binding records;
    // wired/wireless rate enums in header bytes 20/21. Enabled only for matched geometry.
    public ushort[] GetDpisY(OnboardCapabilities caps) => !caps.IsExperimental ? Dpis : Enumerable.Range(0, 5).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(86 + i * 2))).ToArray();
    public void SetDpisY(OnboardCapabilities caps, ushort[] values)
    {
        if (!caps.IsExperimental) { if (!values.SequenceEqual(Dpis)) throw new NotSupportedException("此板载格式没有独立 Y 轴 DPI。"); return; }
        if (values.Length != 5) throw new ArgumentException("必须提供五个 Y 轴 DPI。");
        for (var i = 0; i < 5; i++) BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(86 + i * 2), values[i]);
    }
    public int GetRateHz(OnboardCapabilities caps, bool wireless) => caps.IsExperimental
        ? HidPlusPlus.PerformanceController.RateFromIndex(raw[wireless ? 21 : 20])
        : ReportInterval is 1 or 2 or 4 or 8 ? 1000 / ReportInterval : throw new InvalidDataException("配置的回报率无效。");
    public void SetRateHz(OnboardCapabilities caps, bool wireless, int hz)
    {
        if (caps.IsExperimental) raw[wireless ? 21 : 20] = (byte)HidPlusPlus.PerformanceController.RateIndex(hz);
        else ReportInterval = hz is 125 or 250 or 500 or 1000 ? (byte)(1000 / hz) : throw new ArgumentOutOfRangeException(nameof(hz));
    }

    public ushort PowerSaveTimeout { get => BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(28)); set => BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(28), value); }
    public ushort PowerOffTimeout { get => BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(30)); set => BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(30), value); }
    public byte[] GetBinding(int button, bool gshift = false) => raw.AsSpan(BindingOffset(button, gshift), 4).ToArray();
    public void SetBinding(int button, bool gshift, byte[] binding)
    {
        if (binding.Length != 4) throw new ArgumentException("按键绑定必须是 4 字节。");
        binding.CopyTo(raw, BindingOffset(button, gshift));
    }
    private static int BindingOffset(int index, bool gshift)
    {
        if (index is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(index));
        return (gshift ? 96 : 32) + index * 4;
    }
    public byte[] GetLighting(int slot) => raw.AsSpan(LightingOffset(slot), 11).ToArray();
    public void SetLighting(int slot, byte[] effect)
    {
        if (effect.Length != 11) throw new ArgumentException("灯效记录必须是 11 字节。");
        effect.CopyTo(raw, LightingOffset(slot));
    }
    private static int LightingOffset(int slot)
    {
        if (slot is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(slot));
        return 208 + slot * 11;
    }

    public byte[] ToBytes() => SectorChecksum.Seal((byte[])raw.Clone());
    public MouseProfile Copy() => new(Sector, Enabled, ToBytes(), FromFactory);
    public void RefreshFrom(MouseProfile profile)
    {
        if (Sector != profile.Sector || raw.Length != profile.raw.Length) throw new ArgumentException("配置槽不匹配。");
        profile.raw.CopyTo(raw, 0); Enabled = profile.Enabled;
    }

    public void Validate(OnboardCapabilities capabilities)
    {
        if (!capabilities.CanEditProfiles) throw new NotSupportedException("此设备的板载布局尚未适配，仅允许读取和备份。");
        if (Sector is 0 || Sector > capabilities.ProfileCount) throw new InvalidDataException("配置扇区超出范围。");
        if (capabilities.CanEditPerformance) { _ = GetRateHz(capabilities, false); _ = GetRateHz(capabilities, true); }
        var dpis = Dpis;
        if (DefaultDpiIndex > 4 || dpis[DefaultDpiIndex] is 0 or 65535) throw new InvalidDataException("默认 DPI 必须指向一个启用的 DPI 槽。");
        if (capabilities.HasDpiShift && (ShiftDpiIndex > 4 || dpis[ShiftDpiIndex] is 0 or 65535))
            throw new InvalidDataException("DPI Shift 必须指向一个启用的 DPI 槽。");
        if (!Enumerable.Range(0, capabilities.ButtonCount).Any(i => GetBinding(i).SequenceEqual(ButtonBinding.Mouse(1))))
            throw new InvalidDataException("请至少保留一个普通层按键作为鼠标左键。");
    }
}
