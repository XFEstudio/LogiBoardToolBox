using LogiBoardToolBox.Core.Macros;

namespace LogiBoardToolBox.Core.Onboard;

public sealed record MacroAssignment(int ButtonIndex, bool GShift, CompiledMacro Macro);
public sealed record MacroLocation(ushort Sector, ushort Offset);
public sealed record MacroAllocation(Dictionary<int, byte[]> Sectors, Dictionary<(int Button, bool GShift), MacroLocation> Bindings);

public static class MacroAllocator
{
    /// <summary>Follows both explicit jumps and implicit continuation, including disabled profiles.</summary>
    public static HashSet<int> FindReferencedSectors(MemorySnapshot snapshot)
    {
        var used = new HashSet<int>();
        foreach (var profile in snapshot.GetProfiles())
        {
            // All records are reserved, including fields outside the advertised button count.
            for (var layer = 0; layer < 2; layer++)
            for (var i = 0; i < snapshot.Capabilities.BindingRecordCount; i++)
            {
                var binding = profile.GetBinding(i, layer == 1);
                if (!ButtonBinding.IsMacro(binding)) continue;
                var sector = (int)ButtonBinding.MacroSector(binding);
                var offset = (int)ButtonBinding.MacroOffset(binding);
                var visited = new HashSet<(int, int)>();
                var terminated = false;
                while (visited.Add((sector, offset)))
                {
                    if (sector < 0x100) used.Add(sector);
                    if (!snapshot.Sectors.TryGetValue(sector, out var data) || !SectorChecksum.IsValid(data))
                        throw new InvalidDataException($"现有宏引用了无法校验的扇区 {sector}，无法分配新宏空间。");
                    if (offset < 0 || offset >= data.Length - 2) throw new InvalidDataException("现有宏的地址超出扇区。");
                    var opcode = data[offset];
                    if (opcode == 0xFF) { terminated = true; break; }
                    var length = MacroCompiler.InstructionSize(opcode);
                    if (offset + length > data.Length - 2) { sector++; offset = 0; continue; }
                    if (opcode == 0x60) { var nextOffset = data[offset + 1]; sector = data[offset + 2]; offset = nextOffset; continue; }
                    offset += length;
                    if (offset == data.Length - 2) { sector++; offset = 0; }
                    if (visited.Count > snapshot.Capabilities.SectorCount * snapshot.Capabilities.SectorSize)
                        throw new InvalidDataException("现有宏跳转链过长。");
                }
                if (!terminated) throw new InvalidDataException("现有宏存在循环跳转，无法安全分配新空间。");
            }
        }
        return used;
    }

    public static MacroAllocation Allocate(MemorySnapshot snapshot, IReadOnlyList<MacroAssignment> assignments)
    {
        if (assignments.Count == 0) return new([], []);
        var caps = snapshot.Capabilities;
        if (!caps.CanWriteMacros) throw new NotSupportedException("设备没有可用的格式 1 板载宏空间。");
        if (assignments.Any(a => a.ButtonIndex < 0 || a.ButtonIndex >= caps.AssignableButtonCount || a.GShift && !caps.HasGShift))
            throw new ArgumentException("宏绑定超出此鼠标实际提供的按键或 G-Shift 层。");
        if (assignments.Select(a => (a.ButtonIndex, a.GShift)).Distinct().Count() != assignments.Count)
            throw new ArgumentException("同一实体键和层不能分配多个宏。");
        var used = FindReferencedSectors(snapshot);
        var free = new Queue<int>(Enumerable.Range(caps.ProfileCount + 1, caps.SectorCount - caps.ProfileCount - 1)
            .Where(i => !used.Contains(i) && snapshot.Sectors.TryGetValue(i, out var data) && (SectorChecksum.IsValid(data) || data.All(b => b == 0xFF))));
        var sectors = new Dictionary<int, byte[]>();
        var locations = new Dictionary<(int, bool), MacroLocation>();
        var dedup = new Dictionary<string, MacroLocation>();
        // Reuse identical bytecode already referenced on the device. Automatic saves of a
        // DPI/name edit must not move every unchanged macro or rewrite a macro sector.
        foreach (var profile in snapshot.GetProfiles())
        for (var layer = 0; layer < 2; layer++)
        for (var button = 0; button < caps.BindingRecordCount; button++)
        {
            var binding = profile.GetBinding(button, layer == 1);
            if (!ButtonBinding.IsMacro(binding)) continue;
            var location = new MacroLocation(ButtonBinding.MacroSector(binding), ButtonBinding.MacroOffset(binding));
            var bytes = ReadMacroBytes(snapshot, location);
            // G HUB rounds each macro allocation up to a two-byte boundary. Old
            // versions packed odd-length programs without padding; retaining those
            // addresses would defeat the corrected allocator during a re-import.
            if ((location.Offset & 1) == 0) dedup.TryAdd(Convert.ToHexString(bytes), location);
        }
        var usable = caps.SectorSize - 2;
        var currentSector = -1;
        var currentOffset = 0;

        int NextSector()
        {
            if (!free.TryDequeue(out var sector))
                throw new InvalidOperationException("剩余板载宏空间不足。现有宏会被保留；可先移除旧宏的按键绑定并写入，再保存新宏。");
            sectors[sector] = Enumerable.Repeat((byte)0xFF, caps.SectorSize).ToArray();
            return sector;
        }

        foreach (var assignment in assignments)
        {
            if (assignment.ButtonIndex < 0 || assignment.ButtonIndex >= caps.AssignableButtonCount || assignment.GShift && !caps.HasGShift)
                throw new ArgumentOutOfRangeException(nameof(assignments));
            var instructions = assignment.Macro.Instructions;
            var key = Convert.ToHexString(assignment.Macro.Bytes);
            if (!dedup.TryGetValue(key, out var location))
            {
                if (assignment.Macro.Length <= usable)
                {
                    currentOffset = (currentOffset + 1) & ~1;
                    if (currentSector < 0 || usable - currentOffset < assignment.Macro.Length)
                    { currentSector = NextSector(); currentOffset = 0; }
                    location = new((ushort)currentSector, (ushort)currentOffset);
                    assignment.Macro.Bytes.CopyTo(sectors[currentSector], currentOffset);
                    currentOffset += assignment.Macro.Length;
                }
                else
                {
                    var sector = NextSector();
                    var offset = 0;
                    location = new((ushort)sector, 0);
                    for (var i = 0; i < instructions.Count; i++)
                    {
                        var remaining = instructions.Skip(i).Sum(x => x.Length);
                        if (remaining > usable - offset && instructions[i].Length + 3 > usable - offset)
                        {
                            var next = NextSector();
                            new byte[] { 0x60, 0, (byte)next }.CopyTo(sectors[sector], offset);
                            sector = next;
                            offset = 0;
                        }
                        instructions[i].CopyTo(sectors[sector], offset);
                        offset += instructions[i].Length;
                    }
                }
                dedup[key] = location;
            }
            if (!locations.TryAdd((assignment.ButtonIndex, assignment.GShift), location))
                throw new InvalidOperationException("同一个按键不能重复分配宏。");
        }
        foreach (var data in sectors.Values) SectorChecksum.Seal(data);
        return new(sectors, locations);
    }

    public static byte[] ReadMacroBytes(MemorySnapshot snapshot, MacroLocation location)
    {
        var bytes = new List<byte>(); var sector = (int)location.Sector; var offset = (int)location.Offset;
        var visited = new HashSet<(int, int)>();
        while (visited.Add((sector, offset)))
        {
            if (!snapshot.Sectors.TryGetValue(sector, out var data) || !SectorChecksum.IsValid(data)
                || offset < 0 || offset >= data.Length - 2)
                throw new InvalidDataException("宏地址无效或扇区校验失败。");
            var opcode = data[offset];
            if (opcode == 0xFF) { bytes.Add(0xFF); return bytes.ToArray(); }
            var size = MacroCompiler.InstructionSize(opcode);
            if (offset + size > data.Length - 2) { sector++; offset = 0; continue; }
            if (opcode == 0x60) { var next = data[offset + 2]; offset = data[offset + 1]; sector = next; continue; }
            bytes.AddRange(data.AsSpan(offset, size).ToArray()); offset += size;
            if (offset == data.Length - 2) { sector++; offset = 0; }
        }
        throw new InvalidDataException("现有宏跳转链存在循环。");
    }
}
