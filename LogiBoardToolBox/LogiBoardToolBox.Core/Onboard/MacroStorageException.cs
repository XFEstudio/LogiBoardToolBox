namespace LogiBoardToolBox.Core.Onboard;

/// <summary>A failed storage read, distinct from an unsupported firmware instruction.</summary>
public sealed class MacroStorageException(int sector, int offset, string reason)
    : IOException($"板载宏扇区 {sector}（0x{sector:X4}），偏移 {offset}：{reason}。更改仍保留；可重试保存或导出诊断包。")
{
    public int Sector { get; } = sector;
    public int Offset { get; } = offset;
}
