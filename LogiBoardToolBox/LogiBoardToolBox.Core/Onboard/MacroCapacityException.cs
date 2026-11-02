namespace LogiBoardToolBox.Core.Onboard;

public sealed class MacroCapacityException(int required, int available, int total) : InvalidOperationException(
    $"板载宏空间不足：当前宏指令需要 {required:N0} 字节，可分配 {available:N0} 字节（宏区总计 {total:N0} 字节）。" +
    "跨区跳转和两字节对齐也会占用空间；Lua 原文仍保留，可减少动作或使用软件执行。")
{
    public int RequiredProgramBytes { get; } = required;
    public int AvailableStorageBytes { get; } = available;
    public int TotalStorageBytes { get; } = total;
}
