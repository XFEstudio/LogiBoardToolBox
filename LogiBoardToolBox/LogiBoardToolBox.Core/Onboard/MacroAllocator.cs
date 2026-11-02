using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Diagnostics;

namespace LogiBoardToolBox.Core.Onboard;

public sealed record MacroAssignment(int ButtonIndex, bool GShift, CompiledMacro Macro);
public sealed record MacroLocation(ushort Sector, ushort Offset);
public sealed record MacroAllocation(Dictionary<int, byte[]> Sectors, Dictionary<(int Button, bool GShift), MacroLocation> Bindings);

public static partial class MacroAllocator
{
    /// <summary>Follows both explicit jumps and implicit continuation, including disabled profiles.</summary>
    public static HashSet<int> FindReferencedSectors(MemorySnapshot snapshot) => FindReferencedStorage(snapshot, null);

    private static HashSet<int> FindReferencedStorage(MemorySnapshot snapshot, Dictionary<int, bool[]>? occupied,
        Func<MouseProfile, int, bool, bool>? skip = null)
    {
        var used = new HashSet<int>();
        var validated = new Dictionary<int, byte[]>();
        foreach (var profile in snapshot.GetProfiles())
        {
            // All records are reserved, including fields outside the advertised button count.
            for (var layer = 0; layer < 2; layer++)
            for (var i = 0; i < snapshot.Capabilities.BindingRecordCount; i++)
            {
                if (skip?.Invoke(profile, i, layer == 1) == true) continue;
                var binding = profile.GetBinding(i, layer == 1);
                if (!ButtonBinding.IsMacro(binding)) continue;
                var sector = (int)ButtonBinding.MacroSector(binding);
                var offset = (int)ButtonBinding.MacroOffset(binding);
                var visited = new HashSet<(int, int)>();
                var terminated = false;
                while (visited.Add((sector, offset)))
                {
                    if (sector < 0x100) used.Add(sector);
                    byte[] data;
                    try
                    {
                        if (!validated.TryGetValue(sector, out data!)) validated[sector] = data = ReadStorage(snapshot, sector, offset);
                    }
                    catch (MacroStorageException error)
                    {
                        error.Data["ProfileSector"] = profile.Sector; error.Data["ProfileEnabled"] = profile.Enabled;
                        error.Data["ButtonIndex"] = i; error.Data["GShift"] = layer == 1;
                        error.Data["Binding"] = Convert.ToHexString(binding);
                        DiagnosticLog.Record("macro-allocation", "existing reference failed", new { profile.Sector,
                            profile.Enabled, ButtonIndex = i, GShift = layer == 1, Binding = Convert.ToHexString(binding), TargetSector = sector, offset }, error);
                        throw;
                    }
                    var usable = snapshot.Capabilities.MacroPageCapacity;
                    if (offset < 0 || offset >= usable) throw new InvalidDataException("现有宏的地址超出扇区。");
                    var opcode = data[offset];
                    if (opcode == 0xFF) { Reserve(1); terminated = true; break; }
                    var length = opcode == 0x60 ? snapshot.Capabilities.MacroJumpSize : MacroCompiler.InstructionSize(opcode);
                    if (offset + length > usable) { Reserve(usable - offset); sector++; offset = 0; continue; }
                    Reserve(length);
                    if (opcode == 0x60) { (sector, offset) = JumpTarget(snapshot.Capabilities, data, offset); continue; }
                    offset += length;
                    if (offset == usable) { sector++; offset = 0; }
                    if (visited.Count > snapshot.Capabilities.SectorCount * snapshot.Capabilities.SectorSize)
                        throw new InvalidDataException("现有宏跳转链过长。");
                    void Reserve(int count)
                    {
                        if (occupied is null) return;
                        if (!occupied.TryGetValue(sector, out var mask)) occupied[sector] = mask = new bool[data.Length];
                        Array.Fill(mask, true, offset, count);
                    }
                }
                if (!terminated) throw new InvalidDataException("现有宏存在循环跳转，无法安全分配新空间。");
            }
        }
        return used;
    }

    public static MacroAllocation Allocate(MemorySnapshot snapshot, IReadOnlyList<MacroAssignment> assignments, ushort? profileSector = null)
        => AllocatePacked(snapshot, assignments, profileSector);

    public static byte[] ReadMacroBytes(MemorySnapshot snapshot, MacroLocation location)
    {
        var bytes = new List<byte>(); var sector = (int)location.Sector; var offset = (int)location.Offset;
        var visited = new HashSet<(int, int)>();
        var validated = new Dictionary<int, byte[]>();
        while (visited.Add((sector, offset)))
        {
            if (!validated.TryGetValue(sector, out var data)) validated[sector] = data = ReadStorage(snapshot, sector, offset);
            var usable = snapshot.Capabilities.MacroPageCapacity;
            if (offset < 0 || offset >= usable)
                throw new InvalidDataException("宏地址无效或扇区校验失败。");
            var opcode = data[offset];
            if (opcode == 0xFF) { bytes.Add(0xFF); return bytes.ToArray(); }
            var size = opcode == 0x60 ? snapshot.Capabilities.MacroJumpSize : MacroCompiler.InstructionSize(opcode);
            if (offset + size > usable) { sector++; offset = 0; continue; }
            if (opcode == 0x60) { (sector, offset) = JumpTarget(snapshot.Capabilities, data, offset); continue; }
            bytes.AddRange(data.AsSpan(offset, size).ToArray()); offset += size;
            if (offset == usable) { sector++; offset = 0; }
        }
        throw new InvalidDataException("现有宏跳转链存在循环。");
    }

    private static byte[] ReadStorage(MemorySnapshot snapshot, int sector, int offset)
    {
        if (!snapshot.Sectors.TryGetValue(sector, out var data))
            throw new MacroStorageException(sector, offset, snapshot.ReadErrors.TryGetValue(sector, out var reason)
                ? "扇区读取失败，" + reason : "原始快照未包含此扇区");
        if (data.Length != snapshot.Capabilities.SectorSize)
            throw new MacroStorageException(sector, offset, $"扇区长度 {data.Length} 与设备报告的 {snapshot.Capabilities.SectorSize} 不一致");
        // An erased macro page is all FF, including the unused CRC bytes. FF is
        // the end instruction: this is an empty program, not corrupted bytecode.
        // The caller still reserves this page (and any chain leading to it).
        if (snapshot.Capabilities.IsRawMacroSector(sector) || SectorChecksum.IsValid(data) || sector > snapshot.Capabilities.ProfileCount &&
            sector < snapshot.Capabilities.SectorCount && SectorChecksum.IsErased(data)) return data;
        var stored = Convert.ToHexString(data.AsSpan(data.Length - 2));
        var calculated = SectorChecksum.Compute(data.AsSpan(0, data.Length - 2));
        throw new MacroStorageException(sector, offset, $"CRC 不一致（设备 {stored} / 计算 {calculated:X4}）");
    }

    private static (int Sector, int Offset) JumpTarget(OnboardCapabilities caps, byte[] data, int offset) => caps.UsesLegacyMacroPages
        ? ((data[offset + 1] << 8) | data[offset + 2], (data[offset + 3] << 8) | data[offset + 4])
        : (data[offset + 2], data[offset + 1]);
}
