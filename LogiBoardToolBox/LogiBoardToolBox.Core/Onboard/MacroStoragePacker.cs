using LogiBoardToolBox.Core.Macros;

namespace LogiBoardToolBox.Core.Onboard;

public static partial class MacroAllocator
{
    private readonly record struct FreeBlock(int Sector, int Start, int Length);

    private static MacroAllocation AllocatePacked(MemorySnapshot snapshot, IReadOnlyList<MacroAssignment> assignments, ushort? profileSector)
    {
        if (assignments.Count == 0) return new([], []);
        var caps = snapshot.Capabilities;
        if (!caps.CanWriteMacros) throw new NotSupportedException("设备没有可用的格式 1 板载宏空间。");
        foreach (var assignment in assignments) MacroCompiler.ValidateFirmwareInstructions(assignment.Macro, caps);
        if (assignments.Any(a => a.Macro.Repeat == MacroRepeat.Toggle) && !caps.SupportsNativeToggle)
            throw new NotSupportedException(MacroCompiler.OnboardToggleUnavailable);
        if (assignments.Any(a => a.Macro.Repeat is MacroRepeat.Toggle or MacroRepeat.Continuous && (a.Macro.Instructions.Count < 3 ||
            !a.Macro.Instructions[^2].SequenceEqual(new byte[] { 3 }) || !a.Macro.Instructions[^1].SequenceEqual(new byte[] { 0xFF }))))
            throw new InvalidDataException("宏的固件循环指令无效。");
        if (profileSector is not null && (profileSector == 0 || profileSector > caps.ProfileCount)) throw new ArgumentOutOfRangeException(nameof(profileSector));
        if (assignments.Any(a => a.ButtonIndex < 0 || a.ButtonIndex >= caps.AssignableButtonCount || a.GShift && !caps.HasGShift))
            throw new ArgumentException("宏绑定超出此鼠标实际提供的按键或 G-Shift 层。");
        if (assignments.Select(a => (a.ButtonIndex, a.GShift)).Distinct().Count() != assignments.Count)
            throw new ArgumentException("同一实体键和层不能分配多个宏。");

        // Validate the complete baseline before reclaiming replaced bindings. A bad
        // CRC/reference is still a recovery error, never guessed to be free storage.
        var occupied = new Dictionary<int, bool[]>();
        var originalPages = FindReferencedStorage(snapshot, occupied);
        var cache = new Dictionary<MacroLocation, byte[]>();
        byte[] Program(byte[] b)
        {
            var location = new MacroLocation(ButtonBinding.MacroSector(b), ButtonBinding.MacroOffset(b));
            if (!cache.TryGetValue(location, out var bytes)) cache[location] = bytes = ReadMacroBytes(snapshot, location);
            return bytes;
        }
        var replacements = new HashSet<(int Button, bool Shift)>();
        var selected = snapshot.GetProfiles().FirstOrDefault(p => p.Sector == profileSector);
        if (selected is not null)
            foreach (var a in assignments)
            {
                var old = selected.GetBinding(a.ButtonIndex, a.GShift);
                var same = ButtonBinding.IsMacro(old) && (old[0] >> 4) == (a.Macro.Repeat == MacroRepeat.Toggle ? 3 : 0) &&
                    (ButtonBinding.MacroOffset(old) & 1) == 0 && Program(old).AsSpan().SequenceEqual(a.Macro.Bytes);
                if (!same) replacements.Add((a.ButtonIndex, a.GShift));
            }
        bool Replaced(MouseProfile p, int button, bool shift) => p.Sector == profileSector && replacements.Contains((button, shift));
        if (replacements.Count > 0) { occupied.Clear(); FindReferencedStorage(snapshot, occupied, Replaced); }

        var sectors = new Dictionary<int, byte[]>();
        var locations = new Dictionary<(int, bool), MacroLocation>();
        var dedup = new Dictionary<string, MacroLocation>(StringComparer.Ordinal);
        static string Key(byte[] bytes, bool toggle, int profile, int button, bool shift) => toggle
            ? $"toggle:{profile}:{button}:{shift}:{Convert.ToHexString(bytes)}" : Convert.ToHexString(bytes);
        foreach (var p in snapshot.GetProfiles())
        for (var layer = 0; layer < 2; layer++)
        for (var button = 0; button < caps.BindingRecordCount; button++)
        {
            if (Replaced(p, button, layer == 1)) continue;
            var b = p.GetBinding(button, layer == 1);
            if (!ButtonBinding.IsMacro(b) || b[0] >> 4 == 1 || (ButtonBinding.MacroOffset(b) & 1) != 0) continue;
            dedup.TryAdd(Key(Program(b), ButtonBinding.IsNativeToggle(b), p.Sector, button, layer == 1),
                new(ButtonBinding.MacroSector(b), ButtonBinding.MacroOffset(b)));
        }
        var pages = Enumerable.Range(caps.ProfileCount + 1, caps.SectorCount - caps.ProfileCount - 1)
            .Where(s => snapshot.Sectors.TryGetValue(s, out var data) && caps.IsValidSectorWrite(s, data, allowErased: true)).ToArray();
        foreach (var s in pages) occupied.TryAdd(s, new bool[caps.SectorSize]);
        var total = pages.Length * caps.MacroPageCapacity;
        int Available() => pages.Sum(s => occupied[s].AsSpan(0, caps.MacroPageCapacity).Count(false));
        IEnumerable<FreeBlock> Blocks(bool continuation)
        {
            foreach (var s in pages)
            {
                var mask = occupied[s]; var at = 0;
                while (at < caps.MacroPageCapacity)
                {
                    while (at < caps.MacroPageCapacity && mask[at]) at++;
                    var start = (at + 1) & ~1;
                    while (at < caps.MacroPageCapacity && !mask[at]) at++;
                    if (at > start && (!continuation || caps.UsesLegacyMacroPages || start <= 255)) yield return new(s, start, at - start);
                }
            }
        }
        void Write(int sector, int offset, byte[] bytes)
        {
            if (occupied[sector].AsSpan(offset, bytes.Length).Contains(true)) throw new InvalidDataException("宏分配覆盖了仍被引用的指令。");
            if (!sectors.TryGetValue(sector, out var data)) sectors[sector] = data = (byte[])snapshot.Sectors[sector].Clone();
            bytes.CopyTo(data, offset); Array.Fill(occupied[sector], true, offset, bytes.Length);
        }
        foreach (var assignment in assignments)
        {
            var instructions = assignment.Macro.Instructions; var all = assignment.Macro.Bytes; var length = all.Length;
            if (length == 0 || instructions.Any(i => i.Length == 0 || i[0] == 0x60 || i.Length != MacroCompiler.InstructionSize(i[0], caps)))
                throw new InvalidDataException("待分配宏包含无效指令或未解析的存储跳转。");
            var key = Key(all, assignment.Macro.Repeat == MacroRepeat.Toggle, profileSector ?? 0, assignment.ButtonIndex, assignment.GShift);
            if (!dedup.TryGetValue(key, out var location))
            {
                if (length > Available()) throw new MacroCapacityException(length, Available(), total);
                var index = 0; var remaining = length; location = null!;
                (int Sector, int Offset)? previousJump = null;
                while (index < instructions.Count)
                {
                    var blocks = Blocks(previousJump is not null).ToArray();
                    var whole = blocks.Where(b => b.Length >= remaining).OrderBy(b => originalPages.Contains(b.Sector) ? 1 : 0).ThenBy(b => b.Length).FirstOrDefault();
                    var block = whole.Length > 0 ? whole : blocks.Where(b => b.Length >= instructions[index].Length + caps.MacroJumpSize)
                        .OrderBy(b => originalPages.Contains(b.Sector) ? 1 : 0).ThenByDescending(b => b.Length).FirstOrDefault();
                    if (block.Length == 0) throw new MacroCapacityException(length, Available(), total);
                    location ??= new((ushort)block.Sector, (ushort)block.Start);
                    if (previousJump is { } previous)
                    {
                        var jump = caps.UsesLegacyMacroPages
                            ? new byte[] { 0x60, (byte)(block.Sector >> 8), (byte)block.Sector, (byte)(block.Start >> 8), (byte)block.Start }
                            : [0x60, (byte)block.Start, (byte)block.Sector];
                        // The previous jump's bytes were reserved before choosing a successor.
                        jump.CopyTo(sectors[previous.Sector], previous.Offset);
                    }
                    var at = block.Start; var available = block.Length - (whole.Length > 0 ? 0 : caps.MacroJumpSize);
                    while (index < instructions.Count && instructions[index].Length <= available)
                    {
                        var instruction = instructions[index++]; Write(block.Sector, at, instruction);
                        at += instruction.Length; available -= instruction.Length; remaining -= instruction.Length;
                    }
                    if (index < instructions.Count)
                    { Write(block.Sector, at, new byte[caps.MacroJumpSize]); previousJump = (block.Sector, at); }
                }
                dedup[key] = location;
            }
            locations.Add((assignment.ButtonIndex, assignment.GShift), location);
        }
        foreach (var entry in sectors) if (!caps.IsRawMacroSector(entry.Key)) SectorChecksum.Seal(entry.Value);
        // Exact reuse can produce no changed bytes; avoid needless flash wear.
        foreach (var key in sectors.Keys.ToArray()) if (sectors[key].AsSpan().SequenceEqual(snapshot.Sectors[key])) sectors.Remove(key);
        return new(sectors, locations);
    }
}
